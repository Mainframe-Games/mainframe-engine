using System.Buffers;

namespace SilkTest.Utils;

public sealed class SelectionUI(SelectionUI.Info[] examples)
{
    private int currentSelection;

    private static void ClearScreen()
    {
        Console.Clear();
        Console.SetCursorPosition(0, 0);
    }

    private const string BeginString = "[ ]:";

    /// <summary>
    /// Returns index of selection
    /// </summary>
    /// <returns></returns>
    public Info? Select()
    {
        ClearScreen();
        InitialRender();

        while (true)
        {
            var NothingNew = true;

            do
            {
                var info = Console.ReadKey(true);
                int tmp;
                switch (info.Key)
                {
                    case ConsoleKey.DownArrow:
                        tmp = currentSelection + 1;

                        if (tmp < examples.Length)
                        {
                            currentSelection = tmp;
                        }

                        NothingNew = false;

                        break;

                    case ConsoleKey.UpArrow:
                        tmp = currentSelection - 1;

                        if (tmp >= 0)
                        {
                            currentSelection = tmp;
                        }

                        NothingNew = false;

                        break;

                    case ConsoleKey.Enter:
                        ref var example = ref examples[currentSelection];

                        if (!example.Enabled)
                            break;

                        ClearScreen();

                        return example;

                    case ConsoleKey.Escape:
                        return null;
                }
            } while (Console.KeyAvailable || NothingNew);

            ConsoleHelpers.SetCursorRow(1);
            RenderToConsole();
        }
    }

    private void InitialRender()
    {
        ConsoleHelpers.Draw("Select An Example to Run:");
        RenderToConsole();
    }

    private void RenderToConsole()
    {
        var lineBuffer = ArrayPool<char>.Shared.Rent(ConsoleHelpers.ColumnCount);

        try
        {
            Span<char> buffer = lineBuffer;

            BeginString.AsSpan().CopyTo(buffer);

            var nameBuffer = buffer[(BeginString.Length + 1)..];

            for (var i = 0; i < examples.Length; ++i)
            {
                ref var example = ref examples[i];

                if (i != currentSelection)
                {
                    buffer[1] = ' ';
                }
                else
                {
                    if (example.Enabled)
                        buffer[1] = '-';
                    else
                        buffer[1] = 'x';
                }

                WriteTitleToBuffer(example.Name, nameBuffer);

                ConsoleHelpers.Draw(lineBuffer);

                nameBuffer.Clear();
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(lineBuffer);
        }
    }

    private static void WriteTitleToBuffer(string title, Span<char> buffer)
    {
        ReadOnlySpan<char> tmp = title;

        if (tmp.Length > buffer.Length)
            tmp = tmp[..buffer.Length];

        tmp.CopyTo(buffer);
    }

    public struct Info()
    {
        public string Name { get; init; }
        public bool Enabled { get; init; } = true;
        public Type Type { get; set; }
    }
}
