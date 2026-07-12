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
    public int Health { get; set; }
}

public class BulletInfo
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Dx { get; set; }
    public float Dy { get; set; }
}

public class Form1 : Form
{
    private TcpClient? client;
    private NetworkStream? stream;
    private StreamWriter? writer;

    private int myId = -1;
    private int fieldWidth = 800, fieldHeight = 600;

    private readonly Dictionary<int, PlayerInfo> players = new();
    private readonly Dictionary<int, BulletInfo> bullets = new();
    private readonly Dictionary<int, BulletInfo> localBullets = new();
    private readonly object lockObj = new();
    private readonly System.Windows.Forms.Timer redrawTimer;
    private DateTime lastShootTime = DateTime.MinValue;
    private readonly TimeSpan shootCooldown = TimeSpan.FromMilliseconds(200);
    private PointF lastMousePosition = new();
    private string statusText = "Connecting...";
    private bool socketConnected = false;
    private int nextLocalBulletId = -1;

    public Form1()
    {
        Text = "Multiplayer Shapes";
        ClientSize = new Size(820, 660);
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.White;

        redrawTimer = new System.Windows.Forms.Timer { Interval = 33 }; // ~30 fps
        redrawTimer.Tick += (s, e) => { UpdateLocalBullets(); Invalidate(); };
        redrawTimer.Start();

        KeyDown += Form1_KeyDown;
        MouseDown += Form1_MouseDown;
        MouseMove += Form1_MouseMove;
        Paint += Form1_Paint;
        Load += Form1_Load;
        FormClosing += (s, e) => { try { client?.Close(); } catch { } };
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
            socketConnected = true;
            _ = Task.Run(ReceiveLoop);
            statusText = "Connected to server, waiting for init...";
        }
        catch (Exception ex)
        {
            socketConnected = false;
            statusText = "Connection failed: " + ex.Message;
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
            socketConnected = true;
            statusText = $"Connected as P{myId} ({info.Color} {info.Shape})";
            if (IsHandleCreated)
                Invoke(() => Text = $"Multiplayer Shapes - You are Player {myId} ({info.Color} {info.Shape})");
        }
        else if (type == "state")
        {
            var list = JsonSerializer.Deserialize<List<PlayerInfo>>(root.GetProperty("Players").GetRawText())!;
            var bulletsList = JsonSerializer.Deserialize<List<BulletInfo>>(root.GetProperty("Bullets").GetRawText())!;
            lock (lockObj)
            {
                players.Clear();
                bullets.Clear();
                foreach (var p in list) players[p.Id] = p;
                foreach (var b in bulletsList) bullets[b.Id] = b;
            }
        }
    }

    private void Form1_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            if (myId >= 0)
                SendShoot(lastMousePosition.X, lastMousePosition.Y);
            return;
        }

        float dx = 0, dy = 0;
        const float step = 8f;
        switch (e.KeyCode)
        {
            case Keys.Left: dx = -step; break;
            case Keys.Right: dx = step; break;
            case Keys.Up: dy = -step; break;
            case Keys.Down: dy = step; break;
            default: return;
        }
        SendMove(dx, dy);
    }

    private void Form1_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (myId < 0) return;
        if (e.X < 0 || e.X > fieldWidth || e.Y < 0 || e.Y > fieldHeight) return;
        SendShoot(e.X, e.Y);
    }

    private void Form1_MouseMove(object? sender, MouseEventArgs e)
    {
        lastMousePosition = new PointF(e.X, e.Y);
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

    private void SendShoot(float targetX, float targetY)
    {
        if (writer == null)
        {
            statusText = "Not connected to server";
            return;
        }
        if (DateTime.UtcNow - lastShootTime < shootCooldown)
        {
            statusText = "Shooting too fast";
            return;
        }
        lastShootTime = DateTime.UtcNow;

        float startX = lastMousePosition.X;
        float startY = lastMousePosition.Y;
        if (players.TryGetValue(myId, out var myPlayer))
        {
            startX = myPlayer.X;
            startY = myPlayer.Y;
        }
        float dx = targetX - startX;
        float dy = targetY - startY;
        float length = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
        float normX = dx / length;
        float normY = dy / length;

        localBullets[nextLocalBulletId] = new BulletInfo
        {
            Id = nextLocalBulletId,
            OwnerId = myId,
            X = startX,
            Y = startY,
            Dx = normX * 15f,
            Dy = normY * 15f
        };
        nextLocalBulletId--;

        try
        {
            string msg = JsonSerializer.Serialize(new { Type = "shoot", TargetX = targetX, TargetY = targetY });
            writer.WriteLine(msg);
            statusText = $"Shot fired at {targetX:F0}, {targetY:F0}";
            Console.WriteLine(statusText);
        }
        catch
        {
            statusText = "Could not send shot";
        }
    }

    private void Form1_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawRectangle(Pens.Black, 0, 0, fieldWidth, fieldHeight);
        g.DrawString("Left-click or press Space to shoot", Font, Brushes.DarkBlue, 10, fieldHeight + 10);
        g.DrawString(statusText, Font, Brushes.DarkRed, 10, fieldHeight + 30);

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

            int barWidth = 40;
            int barHeight = 6;
            float healthPercent = Math.Clamp(p.Health / 100f, 0f, 1f);
            var barRect = new RectangleF(rect.Left + (rect.Width - barWidth) / 2f, rect.Top - 14, barWidth, barHeight);
            g.FillRectangle(Brushes.DarkGray, barRect);
            g.FillRectangle(Brushes.LimeGreen, new RectangleF(barRect.Left, barRect.Top, barWidth * healthPercent, barHeight));
            g.DrawRectangle(Pens.Black, barRect.Left, barRect.Top, barRect.Width, barRect.Height);
        }

        var bulletSnapshot = bullets.Values.ToList();
        foreach (var bullet in bulletSnapshot)
        {
            const int bulletSize = 10;
            var bulletRect = new RectangleF(bullet.X - bulletSize / 2f, bullet.Y - bulletSize / 2f, bulletSize, bulletSize);
            using var bulletBrush = new SolidBrush(Color.Black);
            g.FillEllipse(bulletBrush, bulletRect);
            g.DrawEllipse(Pens.DarkGray, bulletRect);
        }

        var localBulletSnapshot = localBullets.Values.ToList();
        foreach (var bullet in localBulletSnapshot)
        {
            const int bulletSize = 10;
            var bulletRect = new RectangleF(bullet.X - bulletSize / 2f, bullet.Y - bulletSize / 2f, bulletSize, bulletSize);
            using var bulletBrush = new SolidBrush(Color.Red);
            g.FillEllipse(bulletBrush, bulletRect);
            g.DrawEllipse(Pens.DarkRed, bulletRect);
        }
    }

    private void UpdateLocalBullets()
    {
        var localSnapshot = localBullets.Values.ToList();
        foreach (var bullet in localSnapshot)
        {
            bullet.X += bullet.Dx;
            bullet.Y += bullet.Dy;
            if (bullet.X < 0 || bullet.X > fieldWidth || bullet.Y < 0 || bullet.Y > fieldHeight)
                localBullets.Remove(bullet.Id);
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
