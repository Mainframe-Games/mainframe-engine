using System.Text;

namespace Neon;

/// <summary>What the terminal can do to the apartment (the apartment implements it; tests use a fake).</summary>
public interface ITerminalHost
{
    /// <summary>The rooms with a light switch, lower case (<c>living</c>, <c>bath</c>, ...).</summary>
    IReadOnlyList<string> Rooms { get; }

    /// <summary>Turns a room's lights on or off; false when there is no such room.</summary>
    bool SetLights(string room, bool on);

    void SetBlinds(bool open);

    /// <summary>Plays a track (0-based), the next one (<c>-2</c>), or stops (<c>-1</c>); the track playing after it, or -1.</summary>
    int Music(int track);

    void SetTv(bool on);

    /// <summary>Vehicles moving in the city right now (for <c>traffic</c>).</summary>
    int TrafficCount { get; }
}

/// <summary>
/// The computer's shell: a scrollback of <see cref="MaxLines"/> lines and an input line. Typing edits the input;
/// <see cref="Enter"/> runs it. Commands: <c>help</c>, <c>whoami</c>, <c>weather</c>, <c>traffic</c>,
/// <c>lights &lt;room|all&gt; on|off</c>, <c>blinds open|close</c>, <c>music play|next|stop</c>, <c>tv on|off</c>,
/// <c>clear</c>, <c>exit</c>. <see cref="Version"/> changes whenever what is shown changes (the screen redraws then);
/// nothing allocates unless a key is pressed.
/// </summary>
public sealed class Terminal(ITerminalHost host)
{
    public const int MaxLines = 13;
    public const int MaxInput = 40;
    public const string Prompt = "kai@unit-2077:~$ ";

    private readonly List<string> _lines = [];
    private readonly StringBuilder _input = new();

    /// <summary>The scrollback, oldest first.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>The prompt and what has been typed so far.</summary>
    public string InputLine { get; private set; } = Prompt;

    /// <summary>Changes whenever <see cref="Lines"/> or <see cref="InputLine"/> do.</summary>
    public int Version { get; private set; }

    /// <summary>Raised by <c>exit</c>: the player stands up.</summary>
    public event Action? ExitRequested;

    /// <summary>Clears the screen and prints the greeting.</summary>
    public void Boot()
    {
        _lines.Clear();
        Print("NEON//OS 7.4.1  (tty1)");
        Print("last login: 23:12 from 10.77.0.4");
        Print("rain advisory in effect. stay indoors.");
        Print("type 'help' for commands.");
        Print("");
    }

    public void Type(char c)
    {
        if (c < ' ' || c > '~' || _input.Length >= MaxInput)
            return;
        _input.Append(c);
        Changed();
    }

    public void Backspace()
    {
        if (_input.Length == 0)
            return;
        _input.Length--;
        Changed();
    }

    /// <summary>Runs the input line.</summary>
    public void Enter()
    {
        var line = _input.ToString();
        _input.Clear();
        Print(Prompt + line);
        Execute(line);
        Changed();
    }

    /// <summary>Runs one command line (as typed, without the prompt).</summary>
    public void Execute(string line)
    {
        var words = line.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return;
        switch (words[0])
        {
            case "help":
                Print("help        this list");
                Print("whoami      who you are");
                Print("weather     outside conditions");
                Print("traffic     vehicles in the grid");
                Print("lights <room|all> on|off");
                Print("blinds open|close");
                Print("music play|next|stop");
                Print("tv on|off   clear   exit");
                break;
            case "whoami":
                Print("kai.tanaka  unit 2077, floor 20, block K-9");
                Print("status: residential curfew (14 days left)");
                break;
            case "weather":
                Print("acid rain 94% . 17 C . wind SW 22 km/h");
                Print("air quality: poor. visibility 1.2 km");
                break;
            case "traffic":
                Print($"{host.TrafficCount} vehicles in sector 9. lanes nominal.");
                break;
            case "lights":
                Lights(words);
                break;
            case "blinds" when words.Length == 2 && words[1] is "open" or "close":
                host.SetBlinds(words[1] == "open");
                Print(words[1] == "open" ? "blinds opening." : "blinds closing.");
                break;
            case "music" when words.Length == 2 && words[1] is "play" or "next" or "stop":
                var track = host.Music(words[1] switch { "play" => 0, "next" => -2, _ => -1 });
                Print(track < 0 ? "music stopped." : $"now playing: track {track + 1}");
                break;
            case "tv" when words.Length == 2 && words[1] is "on" or "off":
                host.SetTv(words[1] == "on");
                Print(words[1] == "on" ? "tv on." : "tv off.");
                break;
            case "clear":
                _lines.Clear();
                break;
            case "exit":
            case "logout":
                Print("logout");
                ExitRequested?.Invoke();
                break;
            default:
                Print($"{words[0]}: command not found or bad arguments");
                break;
        }

        Changed();
    }

    private void Lights(string[] words)
    {
        if (words.Length != 3 || words[2] is not ("on" or "off"))
        {
            Print("usage: lights <" + string.Join('|', host.Rooms) + "|all> on|off");
            return;
        }

        var on = words[2] == "on";
        if (words[1] == "all")
        {
            foreach (var room in host.Rooms)
                host.SetLights(room, on);
            Print(on ? "all lights on." : "all lights off.");
        }
        else if (host.SetLights(words[1], on))
        {
            Print($"{words[1]} lights {words[2]}.");
        }
        else
        {
            Print($"no room '{words[1]}'. rooms: {string.Join(' ', host.Rooms)}");
        }
    }

    private void Print(string line)
    {
        _lines.Add(line);
        while (_lines.Count > MaxLines)
            _lines.RemoveAt(0);
    }

    private void Changed()
    {
        InputLine = Prompt + _input;
        Version++;
    }
}
