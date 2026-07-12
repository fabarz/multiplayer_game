using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

public class PlayerInfo
{
    public int Id { get; set; }
    public string Color { get; set; } = "";
    public string Shape { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
}

public class Form1 : Form
{
    private TcpClient? client;
    private NetworkStream? stream;
    private StreamWriter? writer;

    private int myId = -1;
    private int fieldWidth = 800, fieldHeight = 600;

    private readonly Dictionary<int, PlayerInfo> players = new();
    private readonly object lockObj = new();
    private readonly System.Windows.Forms.Timer redrawTimer;

    // Chat UI Elements
    private TextBox? txtChatHistory;
    private TextBox? txtChatInput;
    private Button? btnSendChat;

    public Form1()
    {
        Text = "Multiplayer Shapes";
        ClientSize = new Size(820, 660);
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.White;

        InitializeChatUI();

        redrawTimer = new System.Windows.Forms.Timer { Interval = 33 }; // ~30 fps
        redrawTimer.Tick += (s, e) => Invalidate();
        redrawTimer.Start();

        KeyDown += Form1_KeyDown;
        Paint += Form1_Paint;
        Load += Form1_Load;
        
        // Fix: Clicking anywhere outside the textboxes shifts focus back to the form so you can play
        MouseDown += Form1_MouseDown;

        FormClosing += (s, e) => { try { client?.Close(); } catch { } };
    }

    private void InitializeChatUI()
    {
        // Chat History Box
        txtChatHistory = new TextBox
        {
            Location = new Point(10, 10),
            Size = new Size(220, 100),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(240, 240, 240),
            TabStop = false
        };

        // Chat Input Box
        txtChatInput = new TextBox
        {
            Location = new Point(10, 115),
            Size = new Size(155, 23),
            TabStop = true
        };
        txtChatInput.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true; // Prevent beep sound
                SendChatMessage();
            }
        };

        // Send Button
        btnSendChat = new Button
        {
            Text = "Send",
            Location = new Point(170, 114),
            Size = new Size(60, 25),
            TabStop = false
        };
        btnSendChat.Click += (s, e) => SendChatMessage();

        Controls.Add(txtChatHistory);
        Controls.Add(txtChatInput);
        Controls.Add(btnSendChat);
    }

    private void Form1_MouseDown(object? sender, MouseEventArgs e)
    {
        // If you click on the game screen, clear active control focus from textboxes
        this.ActiveControl = null;
    }

    private async void Form1_Load(object? sender, EventArgs e)
    {
        string serverIp = PromptForServer();

        try
        {
            client = new TcpClient();
            await client.ConnectAsync(serverIp, 5000);
            stream = client.GetStream();
            writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            _ = Task.Run(ReceiveLoop);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not connect to server: " + ex.Message);
            Close();
        }
    }

    private string PromptForServer()
    {
        using var promptForm = new Form
        {
            Width = 320,
            Height = 150,
            Text = "Connect to server",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };
        var label = new Label { Text = "Server IP address:", Left = 10, Top = 15, Width = 280 };
        var textBox = new TextBox { Left = 10, Top = 40, Width = 280, Text = "127.0.0.1" };
        var button = new Button { Text = "Connect", Left = 110, Top = 70, Width = 80, DialogResult = DialogResult.OK };
        promptForm.Controls.Add(label);
        promptForm.Controls.Add(textBox);
        promptForm.Controls.Add(button);
        promptForm.AcceptButton = button;
        promptForm.ShowDialog();
        return string.IsNullOrWhiteSpace(textBox.Text) ? "127.0.0.1" : textBox.Text.Trim();
    }

    private async Task ReceiveLoop()
    {
        if (stream == null) return;
        var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync();
                if (line == null) break;
                HandleMessage(line);
            }
        }
        catch
        {
            // connection closed
        }
    }

    private void HandleMessage(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        string type = root.GetProperty("Type").GetString() ?? "";

        if (type == "init")
        {
            var info = JsonSerializer.Deserialize<PlayerInfo>(root.GetProperty("Info").GetRawText())!;
            myId = info.Id;
            fieldWidth = root.GetProperty("FieldWidth").GetInt32();
            fieldHeight = root.GetProperty("FieldHeight").GetInt32();
            lock (lockObj) { players[myId] = info; }

            if (IsHandleCreated)
                Invoke(() => Text = $"Multiplayer Shapes - You are Player {myId} ({info.Color} {info.Shape})");
        }
        else if (type == "state")
        {
            var list = JsonSerializer.Deserialize<List<PlayerInfo>>(root.GetProperty("Players").GetRawText())!;
            lock (lockObj)
            {
                players.Clear();
                foreach (var p in list) players[p.Id] = p;
            }
        }
        else if (type == "chat")
        {
            string senderMsg = root.GetProperty("Message").GetString() ?? "";
            Invoke(() =>
            {
                txtChatHistory?.AppendText(senderMsg + Environment.NewLine);
            });
        }
    }

    private void Form1_KeyDown(object? sender, KeyEventArgs e)
    {
        // If the user is intentionally typing into the chat box, don't execute movement controls
        if (txtChatInput != null && txtChatInput.Focused) return;

        float dx = 0, dy = 0;
        const float step = 8f;
        switch (e.KeyCode)
        {
            // Arrow Keys
            case Keys.Left: 
            case Keys.A: 
                dx = -step; break;
                
            case Keys.Right: 
            case Keys.D: 
                dx = step; break;
                
            case Keys.Up: 
            case Keys.W: 
                dy = -step; break;
                
            case Keys.Down: 
            case Keys.S: 
                dy = step; break;
                
            default: return;
        }
        SendMove(dx, dy);
    }

    private void SendMove(float dx, float dy)
    {
        if (writer == null) return;
        try
        {
            string msg = JsonSerializer.Serialize(new { Type = "move", Dx = dx, Dy = dy });
            writer.WriteLine(msg);
        }
        catch
        {
            // ignore send failures, server may have gone away
        }
    }

    private void SendChatMessage()
    {
        if (writer == null || txtChatInput == null || string.IsNullOrWhiteSpace(txtChatInput.Text)) return;

        try
        {
            string cleanText = txtChatInput.Text.Trim();
            string msg = JsonSerializer.Serialize(new { Type = "chat", Message = cleanText });
            writer.WriteLine(msg);
            txtChatInput.Clear();
            
            // Shift focus back to the form so player controls instantly work again after hitting Enter
            this.ActiveControl = null;
        }
        catch
        {
            // ignore send failures
        }
    }

    private void Form1_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawRectangle(Pens.Black, 0, 0, fieldWidth, fieldHeight);

        List<PlayerInfo> snapshot;
        lock (lockObj) { snapshot = players.Values.ToList(); }

        foreach (var p in snapshot)
        {
            Color c = Color.FromName(p.Color);
            using var brush = new SolidBrush(c);
            const int size = 30;
            RectangleF rect = new(p.X - size / 2f, p.Y - size / 2f, size, size);

            switch (p.Shape)
            {
                case "Circle":
                    g.FillEllipse(brush, rect);
                    break;
                case "Square":
                    g.FillRectangle(brush, rect);
                    break;
                case "Triangle":
                    PointF[] tri =
                    {
                        new(rect.Left + rect.Width / 2, rect.Top),
                        new(rect.Left, rect.Bottom),
                        new(rect.Right, rect.Bottom)
                    };
                    g.FillPolygon(brush, tri);
                    break;
                case "Diamond":
                    PointF[] dia =
                    {
                        new(rect.Left + rect.Width / 2, rect.Top),
                        new(rect.Right, rect.Top + rect.Height / 2),
                        new(rect.Left + rect.Width / 2, rect.Bottom),
                        new(rect.Left, rect.Top + rect.Height / 2)
                    };
                    g.FillPolygon(brush, dia);
                    break;
                case "Star":
                    g.FillPolygon(brush, MakeStar(rect));
                    break;
                default:
                    g.FillEllipse(brush, rect);
                    break;
            }

            // Highlight own shape with a border
            if (p.Id == myId)
                g.DrawRectangle(Pens.Black, rect.Left - 2, rect.Top - 2, rect.Width + 4, rect.Height + 4);

            g.DrawString($"P{p.Id}", Font, Brushes.Black, rect.Left, rect.Bottom + 2);
        }
    }

    private static PointF[] MakeStar(RectangleF rect)
    {
        var points = new List<PointF>();
        float cx = rect.Left + rect.Width / 2, cy = rect.Top + rect.Height / 2;
        float outerR = rect.Width / 2, innerR = outerR / 2.5f;
        for (int i = 0; i < 10; i++)
        {
            double angle = Math.PI / 5 * i - Math.PI / 2;
            float r = i % 2 == 0 ? outerR : innerR;
            points.Add(new PointF(cx + (float)(r * Math.Cos(angle)), cy + (float)(r * Math.Sin(angle))));
        }
        return points.ToArray();
    }
}