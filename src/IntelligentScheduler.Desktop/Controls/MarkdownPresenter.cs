using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace IntelligentScheduler.Desktop.Controls;

/// <summary>
/// Renders the small slice of Markdown that language models actually produce: headings,
/// bullet and numbered lists, fenced code, inline code, bold, italic, strike-through and
/// horizontal rules. Anything it does not recognise is shown verbatim, so a reply is never
/// swallowed by the formatter.
/// </summary>
public sealed class MarkdownPresenter : ContentControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownPresenter, string?>(nameof(Text));

    /// <summary>Body colour; headings and code inherit from it.</summary>
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<MarkdownPresenter, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<MarkdownPresenter, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> CodeBackgroundProperty =
        AvaloniaProperty.Register<MarkdownPresenter, IBrush?>(nameof(CodeBackground));

    public static readonly StyledProperty<double> BodyFontSizeProperty =
        AvaloniaProperty.Register<MarkdownPresenter, double>(nameof(BodyFontSize), 12.5);

    /// <summary>
    /// What the button on a fenced block is called. Supplied rather than looked up, because
    /// this control knows nothing about the string table and should not have to.
    /// </summary>
    public static readonly StyledProperty<string?> CopyLabelProperty =
        AvaloniaProperty.Register<MarkdownPresenter, string?>(nameof(CopyLabel));

    static MarkdownPresenter()
    {
        TextProperty.Changed.AddClassHandler<MarkdownPresenter>((presenter, _) => presenter.Rebuild());
        TextBrushProperty.Changed.AddClassHandler<MarkdownPresenter>((presenter, _) => presenter.Rebuild());
        AccentBrushProperty.Changed.AddClassHandler<MarkdownPresenter>((presenter, _) => presenter.Rebuild());
        CodeBackgroundProperty.Changed.AddClassHandler<MarkdownPresenter>((presenter, _) => presenter.Rebuild());
        BodyFontSizeProperty.Changed.AddClassHandler<MarkdownPresenter>((presenter, _) => presenter.Rebuild());
        CopyLabelProperty.Changed.AddClassHandler<MarkdownPresenter>((presenter, _) => presenter.Rebuild());
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush? CodeBackground
    {
        get => GetValue(CodeBackgroundProperty);
        set => SetValue(CodeBackgroundProperty, value);
    }

    public double BodyFontSize
    {
        get => GetValue(BodyFontSizeProperty);
        set => SetValue(BodyFontSizeProperty, value);
    }

    public string? CopyLabel
    {
        get => GetValue(CopyLabelProperty);
        set => SetValue(CopyLabelProperty, value);
    }

    private static readonly Regex OrderedItem = new(@"^\s{0,3}(\d{1,3})[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletItem = new(@"^\s{0,3}[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^(#{1,4})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s{0,3}([-*_])\1{2,}\s*$", RegexOptions.Compiled);

    private void Rebuild()
    {
        var source = Text;
        if (string.IsNullOrWhiteSpace(source))
        {
            Content = null;
            return;
        }

        var stack = new StackPanel { Spacing = 6 };
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            stack.Children.Add(Paragraph(string.Join(" ", paragraph)));
            paragraph.Clear();
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var language = line.Trim().TrimStart('`').Trim();
                var code = new StringBuilder();
                index++;
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[index]);
                    index++;
                }
                stack.Children.Add(CodeBlock(code.ToString().TrimEnd(), language));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); continue; }

            if (Rule.IsMatch(line))
            {
                FlushParagraph();
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 3),
                    Background = Dim(0.28)
                });
                continue;
            }

            if (Heading.Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                var level = heading.Groups[1].Value.Length;
                var block = Paragraph(heading.Groups[2].Value);
                block.FontWeight = FontWeight.SemiBold;
                block.FontSize = BodyFontSize + (level == 1 ? 3 : level == 2 ? 2 : 1);
                block.Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 4, 0, 0);
                stack.Children.Add(block);
                continue;
            }

            if (BulletItem.Match(line) is { Success: true } bullet)
            {
                FlushParagraph();
                stack.Children.Add(ListItem("•", bullet.Groups[1].Value));
                continue;
            }

            if (OrderedItem.Match(line) is { Success: true } ordered)
            {
                FlushParagraph();
                stack.Children.Add(ListItem(ordered.Groups[1].Value + ".", ordered.Groups[2].Value));
                continue;
            }

            if (line.TrimStart().StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                stack.Children.Add(Quote(line.TrimStart()[2..]));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        FlushParagraph();
        Content = stack;
    }

    /// <summary>
    /// One run of prose. Selectable, because a reply is something to take things out of: a
    /// date, a name, a line of a list. Selection runs within a block rather than across them —
    /// each paragraph, item and quote is its own — and the whole reply is still one press away
    /// on the copy button beside it.
    /// </summary>
    private TextBlock Paragraph(string text)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = BodyFontSize,
            LineHeight = BodyFontSize + 6,
            Foreground = TextBrush
        };
        FillInlines(block.Inlines!, text);
        return block;
    }

    private Control ListItem(string marker, string text)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(2, 0, 0, 0)
        };
        var glyph = new TextBlock
        {
            Text = marker,
            FontSize = BodyFontSize,
            LineHeight = BodyFontSize + 6,
            MinWidth = 16,
            Foreground = AccentBrush ?? TextBrush,
            VerticalAlignment = VerticalAlignment.Top
        };
        var body = Paragraph(text);
        Grid.SetColumn(body, 1);
        grid.Children.Add(glyph);
        grid.Children.Add(body);
        return grid;
    }

    private Control Quote(string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2,*") };
        var rail = new Border
        {
            Background = AccentBrush ?? Dim(0.5),
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 1, 8, 1)
        };
        var body = Paragraph(text);
        body.Opacity = 0.85;
        Grid.SetColumn(body, 1);
        grid.Children.Add(rail);
        grid.Children.Add(body);
        return grid;
    }

    /// <summary>
    /// A fenced block, with the one thing anybody wants to do to a fenced block.
    ///
    /// Selecting a command out of a wrapped code block by dragging is the sort of task that
    /// works on the second attempt: the text wraps, so a drag that leaves the block takes the
    /// paragraph under it as well, and what lands on the clipboard has a stray newline in the
    /// middle of it. The button takes exactly the block and nothing else. It sits in a bar over
    /// the code with the language beside it, so it never covers a line, and the bar is drawn
    /// whether or not the fence named a language — a block with no header is a block whose
    /// button has nowhere to live.
    /// </summary>
    private Control CodeBlock(string code, string language)
    {
        var copy = new Button
        {
            Content = new TextBlock { Text = CopyLabel ?? "Copy", FontSize = BodyFontSize * 9.5 / 12.5 },
            Padding = new Thickness(7, 1),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = TextBrush,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(code);
            }
            catch (Exception)
            {
                // A clipboard another process is holding can refuse; the block is unaffected.
            }
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = language,
            FontSize = BodyFontSize * 9.5 / 12.5,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextBrush
        });
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var body = new SelectableTextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            FontSize = BodyFontSize - 1,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextBrush
        };

        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(header);
        stack.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body
        });

        return new Border
        {
            Background = CodeBackground ?? Dim(0.14),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 6, 6, 7),
            Child = stack
        };
    }

    /// <summary>Walks the line once, opening and closing runs as markers appear.</summary>
    private void FillInlines(InlineCollection inlines, string text)
    {
        var buffer = new StringBuilder();
        var bold = false;
        var italic = false;
        var strike = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            inlines.Add(new Run(buffer.ToString())
            {
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
                TextDecorations = strike ? TextDecorations.Strikethrough : null
            });
            buffer.Clear();
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character == '`')
            {
                var close = text.IndexOf('`', index + 1);
                if (close > index)
                {
                    Flush();
                    inlines.Add(new Run(text[(index + 1)..close])
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                        Foreground = AccentBrush ?? TextBrush
                    });
                    index = close;
                    continue;
                }
            }

            if (character == '*' && index + 1 < text.Length && text[index + 1] == '*')
            {
                Flush();
                bold = !bold;
                index++;
                continue;
            }

            if (character == '~' && index + 1 < text.Length && text[index + 1] == '~')
            {
                Flush();
                strike = !strike;
                index++;
                continue;
            }

            // A lone asterisk or underscore only italicises when it is not part of a word.
            if ((character == '*' || character == '_') && IsEmphasisBoundary(text, index))
            {
                Flush();
                italic = !italic;
                continue;
            }

            buffer.Append(character);
        }

        Flush();
        if (inlines.Count == 0) inlines.Add(new Run(text));
    }

    private static bool IsEmphasisBoundary(string text, int index)
    {
        var before = index == 0 ? ' ' : text[index - 1];
        var after = index + 1 >= text.Length ? ' ' : text[index + 1];
        return !char.IsLetterOrDigit(before) || !char.IsLetterOrDigit(after);
    }

    private IBrush Dim(double opacity)
        => new SolidColorBrush(Colors.White, opacity);
}
