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

// ── Data models ───────────────────────────────────────────────────────────────

public class PlayerInfo
{
    public int    Id    { get; set; }
    public string Color { get; set; } = "";
    public string Shape { get; set; } = "";
    public float  X     { get; set; }
    public float  Y     { get; set; }
    public int    Score { get; set; }   // ← NEW: tracks coins + bullet hits
}

// ── NEW: Bullet data received from server ─────────────────────────────────────
public class BulletInfo
{
    public int   Id      { get; set; }
    public int   OwnerId { get; set; }
    public float X       { get; set; }
    public float Y       { get; set; }
}

// ── NEW: Coin data received from server ───────────────────────────────────────
public class CoinInfo
{
    public int   Id { get; set; }
    public float X  { get; set; }
    public float Y  { get; set; }
}

// ── Main Form ─────────────────────────────────────────────────────────────────

public class Form1 : Form
{
    private TcpClient?     client;
    private NetworkStream? stream;
    private StreamWriter?  writer;

    private int myId = -1;
    private int fieldWidth = 800, fieldHeight = 600;

    private readonly Dictionary<int, PlayerInfo> players = new();
    private readonly object lockObj = new();
    private readonly System.Windows.Forms.Timer redrawTimer;

    // ── NEW: Bullets and coins ────────────────────────────────────────────────
    private List<BulletInfo> bullets = new();
    private List<CoinInfo>   coins   = new();

    // ── NEW: Last movement direction so spacebar knows which way to shoot ─────
    private float lastDx = 1f;   // default: shoot right
    private float lastDy = 0f;

    public Form1()
    {
        Text          = "Multiplayer Shapes";
        ClientSize    = new Size(820, 660);
        DoubleBuffered = true;
        KeyPreview    = true;
        BackColor     = Color.White;

        redrawTimer = new System.Windows.Forms.Timer { Interval = 33 }; // ~30 fps
        redrawTimer.Tick += (s, e) => Invalidate();
        redrawTimer.Start();

        KeyDown      += Form1_KeyDown;
        Paint        += Form1_Paint;
        Load         += Form1_Load;
        FormClosing  += (s, e) => { try { client?.Close(); } catch { } };
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
            Width           = 320,
            Height          = 150,
            Text            = "Connect to server",
            StartPosition   = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox     = false,
            MinimizeBox     = false
        };

        var label   = new Label   { Text = "Server IP address:", Left = 10, Top = 15, Width = 280 };
        var textBox = new TextBox { Left = 10, Top = 40, Width = 280, Text = "127.0.0.1" };
        var button  = new Button  { Text = "Connect", Left = 110, Top = 70, Width = 80, DialogResult = DialogResult.OK };

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
        using var doc  = JsonDocument.Parse(line);
        var       root = doc.RootElement;
        string    type = root.GetProperty("Type").GetString() ?? "";

        if (type == "init")
        {
            var info = JsonSerializer.Deserialize<PlayerInfo>(
                root.GetProperty("Info").GetRawText())!;

            myId        = info.Id;
            fieldWidth  = root.GetProperty("FieldWidth").GetInt32();
            fieldHeight = root.GetProperty("FieldHeight").GetInt32();

            lock (lockObj) { players[myId] = info; }

            if (IsHandleCreated)
                Invoke(() => Text =
                    $"Multiplayer Shapes - You are Player {myId} ({info.Color} {info.Shape})");
        }
        else if (type == "state")
        {
            var list = JsonSerializer.Deserialize<List<PlayerInfo>>(
                root.GetProperty("Players").GetRawText())!;

            lock (lockObj)
            {
                players.Clear();
                foreach (var p in list) players[p.Id] = p;
            }
        }

        // ── NEW: handle bullet + coin updates from server ─────────────────────
        else if (type == "gameobjects")
        {
            var newBullets = JsonSerializer.Deserialize<List<BulletInfo>>(
                root.GetProperty("Bullets").GetRawText())!;

            var newCoins = JsonSerializer.Deserialize<List<CoinInfo>>(
                root.GetProperty("Coins").GetRawText())!;

            lock (lockObj)
            {
                bullets = newBullets;
                coins   = newCoins;
            }
        }
    }

    // ── NEW: expanded KeyDown — tracks direction AND fires on Spacebar ────────
    private void Form1_KeyDown(object? sender, KeyEventArgs e)
    {
        float dx = 0, dy = 0;
        const float step = 8f;

        switch (e.KeyCode)
        {
            case Keys.Left:  dx = -step; break;
            case Keys.Right: dx =  step; break;
            case Keys.Up:    dy = -step; break;
            case Keys.Down:  dy =  step; break;

            case Keys.Space:
                // Fire a bullet in the last direction the player was moving
                SendShoot();
                return;

            default: return;
        }

        // Remember direction so spacebar shoots the right way
        lastDx = dx;
        lastDy = dy;

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
            // ignore send failures
        }
    }

    // ── NEW: sends a shoot command to the server ──────────────────────────────
    private void SendShoot()
    {
        if (writer == null) return;
        try
        {
            string msg = JsonSerializer.Serialize(new
            {
                Type = "shoot",
                Dx   = lastDx,
                Dy   = lastDy
            });
            writer.WriteLine(msg);
        }
        catch { }
    }

    private void Form1_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawRectangle(Pens.Black, 0, 0, fieldWidth, fieldHeight);

        List<PlayerInfo> snapshot;
        lock (lockObj) { snapshot = players.Values.ToList(); }

        // ── Draw players (your original logic, unchanged) ─────────────────────
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
                        new(rect.Left,  rect.Bottom),
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
                        new(rect.Left,  rect.Top + rect.Height / 2)
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

            // White border around your own shape
            if (p.Id == myId)
                g.DrawRectangle(Pens.Black,
                    rect.Left - 2, rect.Top - 2,
                    rect.Width + 4, rect.Height + 4);

            // Player label
            g.DrawString($"P{p.Id}", Font, Brushes.Black, rect.Left, rect.Bottom + 2);

            // ── NEW: score shown below the player label ────────────────────────
            g.DrawString($"Score: {p.Score}", Font, Brushes.DarkBlue,
                rect.Left, rect.Bottom + 14);
        }

        // ── NEW: draw coins ───────────────────────────────────────────────────
        List<CoinInfo>   coinSnap;
        List<BulletInfo> bulletSnap;
        lock (lockObj)
        {
            coinSnap   = coins.ToList();
            bulletSnap = bullets.ToList();
        }

        foreach (var coin in coinSnap)
        {
            const int coinSize = 14;

            using var coinBrush = new SolidBrush(Color.Gold);
            g.FillEllipse(coinBrush,
                coin.X - coinSize / 2f,
                coin.Y - coinSize / 2f,
                coinSize, coinSize);

            g.DrawEllipse(Pens.DarkGoldenrod,
                coin.X - coinSize / 2f,
                coin.Y - coinSize / 2f,
                coinSize, coinSize);
        }

        // ── NEW: draw bullets ─────────────────────────────────────────────────
        foreach (var bullet in bulletSnap)
        {
            const int bSize = 7;

            // Colour the bullet to match its owner
            Color bulletColor = Color.White;
            lock (lockObj)
            {
                if (players.TryGetValue(bullet.OwnerId, out var owner))
                    bulletColor = Color.FromName(owner.Color);
            }

            using var bulletBrush = new SolidBrush(bulletColor);
            g.FillEllipse(bulletBrush,
                bullet.X - bSize / 2f,
                bullet.Y - bSize / 2f,
                bSize, bSize);

            // Bright white centre dot for a glowing effect
            g.FillEllipse(Brushes.White,
                bullet.X - 2, bullet.Y - 2, 4, 4);
        }
    }

    private static PointF[] MakeStar(RectangleF rect)
    {
        var   points = new List<PointF>();
        float cx     = rect.Left + rect.Width  / 2;
        float cy     = rect.Top  + rect.Height / 2;
        float outerR = rect.Width / 2;
        float innerR = outerR / 2.5f;

        for (int i = 0; i < 10; i++)
        {
            double angle = Math.PI / 5 * i - Math.PI / 2;
            float  r     = i % 2 == 0 ? outerR : innerR;
            points.Add(new PointF(
                cx + (float)(r * Math.Cos(angle)),
                cy + (float)(r * Math.Sin(angle))));
        }
        return points.ToArray();
    }
}