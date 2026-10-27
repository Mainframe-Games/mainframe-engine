namespace Neon.Tests;

/// <summary>The computer's shell (<see cref="Terminal"/>) against a fake apartment.</summary>
public sealed class TerminalTests
{
    private sealed class FakeHost : ITerminalHost
    {
        public Dictionary<string, bool> Lights { get; } = new() { ["living"] = true, ["bath"] = true };

        public bool? Blinds;
        public int Track = -1;
        public bool Tv;

        public IReadOnlyList<string> Rooms => [.. Lights.Keys];

        public int TrafficCount => 42;

        public bool SetLights(string room, bool on)
        {
            if (!Lights.ContainsKey(room))
                return false;
            Lights[room] = on;
            return true;
        }

        public void SetBlinds(bool open) => Blinds = open;

        public int Music(int track) => Track = track == -2 ? (Track + 1) % 2 : track;

        public void SetTv(bool on) => Tv = on;
    }

    private static (Terminal Terminal, FakeHost Host) Make()
    {
        var host = new FakeHost();
        var terminal = new Terminal(host);
        terminal.Boot();
        return (terminal, host);
    }

    private static void Run(Terminal terminal, string line)
    {
        foreach (var c in line)
            terminal.Type(c);
        terminal.Enter();
    }

    [Fact]
    public void TypingEditsTheInputLineAndEnterRunsIt()
    {
        var (terminal, _) = Make();
        var version = terminal.Version;
        terminal.Type('h');
        terminal.Type('x');
        terminal.Backspace();
        terminal.Type('\n'); // control characters are ignored
        Assert.Equal(Terminal.Prompt + "h", terminal.InputLine);
        Assert.True(terminal.Version > version);
        terminal.Type('e');
        terminal.Type('l');
        terminal.Type('p');
        terminal.Enter();
        Assert.Equal(Terminal.Prompt, terminal.InputLine);
        Assert.Contains(terminal.Lines, l => l.StartsWith("lights <room|all>", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandsDriveTheApartment()
    {
        var (terminal, host) = Make();
        Run(terminal, "lights bath off");
        Assert.False(host.Lights["bath"]);
        Run(terminal, "LIGHTS ALL OFF");
        Assert.All(host.Lights.Values, Assert.False);
        Run(terminal, "blinds close");
        Assert.False(host.Blinds);
        Run(terminal, "music play");
        Assert.Equal(0, host.Track);
        Run(terminal, "music next");
        Assert.Equal(1, host.Track);
        Run(terminal, "music stop");
        Assert.Equal(-1, host.Track);
        Run(terminal, "tv on");
        Assert.True(host.Tv);
        Run(terminal, "traffic");
        Assert.Contains(terminal.Lines, l => l.StartsWith("42 vehicles", StringComparison.Ordinal));
    }

    [Fact]
    public void BadInputExplainsItselfAndExitAsksToStand()
    {
        var (terminal, _) = Make();
        Run(terminal, "lights kitchen on");
        Assert.Contains(terminal.Lines, l => l.StartsWith("no room 'kitchen'", StringComparison.Ordinal));
        Run(terminal, "hack the planet");
        Assert.Contains(terminal.Lines, l => l.StartsWith("hack: command not found", StringComparison.Ordinal));
        var exits = 0;
        terminal.ExitRequested += () => exits++;
        Run(terminal, "exit");
        Assert.Equal(1, exits);
    }

    [Fact]
    public void TheScrollbackKeepsTheLastLinesAndClearEmptiesIt()
    {
        var (terminal, _) = Make();
        for (var i = 0; i < 20; i++)
            Run(terminal, "whoami");
        Assert.Equal(Terminal.MaxLines, terminal.Lines.Count);
        Run(terminal, "clear");
        Assert.Empty(terminal.Lines);
        for (var i = 0; i < Terminal.MaxInput + 10; i++)
            terminal.Type('x');
        Assert.Equal(Terminal.Prompt.Length + Terminal.MaxInput, terminal.InputLine.Length);
    }
}
