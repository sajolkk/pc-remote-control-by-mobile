using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using Xunit;

namespace RemoteAgent.Protocol.Tests;

/// <summary>
/// Wire-format tests for the input, window and file messages.
/// </summary>
/// <remarks>
/// The phone is written against the contract's field names by other people, at the same time. A
/// field this side spells differently is a feature that fails on a real phone and nowhere else, so
/// every test here works from the literal JSON the contract describes: requests are parsed from
/// text a phone would send, and results are serialised and compared key by key.
/// </remarks>
public sealed class RemoteControlMessageTests
{
    private static T Parse<T>(string json)
        where T : class
    {
        T? value = JsonSerializer.Deserialize<T>(json, ProtocolJson.Options);
        Assert.NotNull(value);
        return value!;
    }

    private static JsonObject Serialize<T>(T value) =>
        Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(value, ProtocolJson.Options));

    private static string[] Keys(JsonObject json) => [.. json.Select(static pair => pair.Key)];

    // ---------------------------------------------------------------------
    // Input arguments
    // ---------------------------------------------------------------------

    [Fact]
    public void MouseMoveReadsAbsoluteAndRelativeForms()
    {
        MouseMoveArgs absolute = Parse<MouseMoveArgs>("""{"x":0.25,"y":0.75,"monitorId":"mon-1a2b3c"}""");
        Assert.Equal(0.25, absolute.X);
        Assert.Equal(0.75, absolute.Y);
        Assert.Equal("mon-1a2b3c", absolute.MonitorId);
        Assert.Null(absolute.Dx);
        Assert.Null(absolute.Dy);

        MouseMoveArgs relative = Parse<MouseMoveArgs>("""{"dx":-12,"dy":7}""");
        Assert.Null(relative.X);
        Assert.Null(relative.Y);
        Assert.Null(relative.MonitorId);
        Assert.Equal(-12, relative.Dx);
        Assert.Equal(7, relative.Dy);
    }

    [Fact]
    public void WholeNumberCoordinatesAndFractionalDeltasAreBothAccepted()
    {
        // Dart encodes the double 1.0 as "1.0" and an int as "1". Both must read, in both places.
        MouseMoveArgs corner = Parse<MouseMoveArgs>("""{"x":1,"y":0}""");
        Assert.Equal(1.0, corner.X);
        Assert.Equal(0.0, corner.Y);

        MouseMoveArgs delta = Parse<MouseMoveArgs>("""{"dx":3.0,"dy":-2.6}""");
        Assert.Equal(3, delta.Dx);
        Assert.Equal(-3, delta.Dy);

        ScrollArgs scroll = Parse<ScrollArgs>("""{"dx":0.0,"dy":120.0}""");
        Assert.Equal(0, scroll.Dx);
        Assert.Equal(120, scroll.Dy);
    }

    [Theory]
    [InlineData("""{"x":"NaN","y":0.5}""")]
    [InlineData("""{"x":NaN,"y":0.5}""")]
    [InlineData("""{"x":Infinity,"y":0.5}""")]
    [InlineData("""{"x":"0.5","y":0.5}""")]
    [InlineData("""{"dx":"3","dy":0}""")]
    [InlineData("""{"dx":1e40,"dy":0}""")]
    [InlineData("""{"dx":true,"dy":0}""")]
    public void NonNumericCoordinatesFailToParseRatherThanBecomeZero(string json)
    {
        var context = new CommandContext
        {
            RequestId = "r",
            Command = CommandNames.InputMouseMove,
            Args = TryParseElement(json),
            Caller = new CallerIdentity("d", "n", PermissionSet.All, CommandStage.Authenticated, "c", "127.0.0.1"),
            Descriptor = new CommandDescriptor(CommandNames.InputMouseMove, Permission.ControlInput, CommandTarget.Session),
        };

        // Either the JSON itself is not JSON (NaN, Infinity), or it is and the typed read refuses.
        if (context.Args is null)
        {
            return;
        }

        Assert.False(context.TryReadArgs(out MouseMoveArgs _, out CommandResult? failure));
        Assert.Equal(ErrorCodes.InvalidArguments, failure!.Error!.Code);
    }

    private static JsonElement? TryParseElement(string json)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [Fact]
    public void MouseButtonAndClickReadTheContractFields()
    {
        MouseButtonArgs button = Parse<MouseButtonArgs>("""{"button":"left","down":true,"x":0.1,"y":0.2,"monitorId":"m"}""");
        Assert.Equal("left", button.Button);
        Assert.True(button.Down);
        Assert.Equal(0.1, button.X);
        Assert.Equal(0.2, button.Y);
        Assert.Equal("m", button.MonitorId);

        MouseButtonArgs release = Parse<MouseButtonArgs>("""{"button":"middle","down":false}""");
        Assert.False(release.Down);
        Assert.Null(release.X);

        // "down" absent is absent, not false: the controller refuses it instead of releasing.
        Assert.Null(Parse<MouseButtonArgs>("""{"button":"left"}""").Down);

        MouseClickArgs click = Parse<MouseClickArgs>("""{"button":"right","count":2,"x":0.5,"y":0.5}""");
        Assert.Equal("right", click.Button);
        Assert.Equal(2, click.Count);
        Assert.Null(click.MonitorId);

        Assert.Null(Parse<MouseClickArgs>("""{"button":"left"}""").Count);
    }

    [Fact]
    public void ScrollKeyTextAndShortcutReadTheContractFields()
    {
        ScrollArgs scroll = Parse<ScrollArgs>("""{"dx":0,"dy":-360,"x":0.5,"y":0.5,"monitorId":"m"}""");
        Assert.Equal(0, scroll.Dx);
        Assert.Equal(-360, scroll.Dy);
        Assert.Equal("m", scroll.MonitorId);

        KeyArgs key = Parse<KeyArgs>("""{"vk":13,"down":true}""");
        Assert.Equal(13, key.VirtualKey);
        Assert.True(key.Down);
        Assert.Null(Parse<KeyArgs>("""{"down":true}""").VirtualKey);

        TextArgs text = Parse<TextArgs>("""{"text":"héllo\n\tworld 😀"}""");
        Assert.Equal("héllo\n\tworld \U0001F600", text.Text);

        ShortcutArgs shortcut = Parse<ShortcutArgs>("""{"keys":[17,16,27]}""");
        Assert.Equal([17, 16, 27], shortcut.Keys!);
    }

    [Fact]
    public void FieldNamesAreCaseSensitiveLikeTheRestOfTheProtocol()
    {
        // The options do not match case-insensitively, so a misspelt field is simply absent.
        KeyArgs wrong = Parse<KeyArgs>("""{"VK":13,"Down":true}""");

        Assert.Null(wrong.VirtualKey);
        Assert.Null(wrong.Down);
    }

    [Fact]
    public void InputArgumentsSerialiseBackUnderTheSameNames()
    {
        Assert.Equal(
            ["x", "y", "monitorId"],
            Keys(Serialize(new MouseMoveArgs { X = 0.5, Y = 0.5, MonitorId = "m" })));

        Assert.Equal(["dx", "dy"], Keys(Serialize(new MouseMoveArgs { Dx = 1, Dy = 2 })));

        Assert.Equal(
            ["button", "down", "x", "y", "monitorId"],
            Keys(Serialize(new MouseButtonArgs { Button = "left", Down = true, X = 0, Y = 0, MonitorId = "m" })));

        Assert.Equal(
            ["button", "count", "x", "y", "monitorId"],
            Keys(Serialize(new MouseClickArgs { Button = "left", Count = 2, X = 0, Y = 0, MonitorId = "m" })));

        Assert.Equal(
            ["dx", "dy", "x", "y", "monitorId"],
            Keys(Serialize(new ScrollArgs { Dx = 1, Dy = 2, X = 0, Y = 0, MonitorId = "m" })));

        Assert.Equal(["vk", "down"], Keys(Serialize(new KeyArgs { VirtualKey = 65, Down = false })));
        Assert.Equal(["text"], Keys(Serialize(new TextArgs { Text = "a" })));
        Assert.Equal(["keys"], Keys(Serialize(new ShortcutArgs { Keys = [17, 67] })));
    }

    // ---------------------------------------------------------------------
    // Windows
    // ---------------------------------------------------------------------

    [Fact]
    public void WindowListResultHasTheContractShape()
    {
        JsonObject json = Serialize(new WindowListResult
        {
            Windows =
            [
                new WindowInfo
                {
                    Id = "w1a2b-3c4",
                    Title = "Untitled — Notepad",
                    ProcessName = "notepad",
                    IsMinimized = false,
                    IsMaximized = true,
                    IsForeground = true,
                },
            ],
        });

        Assert.Equal(["windows"], Keys(json));

        JsonObject window = Assert.IsType<JsonObject>(json["windows"]![0]);
        Assert.Equal(["id", "title", "processName", "isMinimized", "isMaximized", "isForeground"], Keys(window));
        Assert.Equal("w1a2b-3c4", window["id"]!.GetValue<string>());
        Assert.Equal("Untitled — Notepad", window["title"]!.GetValue<string>());
        Assert.False(window["isMinimized"]!.GetValue<bool>());
        Assert.True(window["isMaximized"]!.GetValue<bool>());

        WindowListResult back = Parse<WindowListResult>(json.ToJsonString(ProtocolJson.Options));
        Assert.Equal("notepad", Assert.Single(back.Windows).ProcessName);
    }

    [Fact]
    public void WindowActionArgsAndResultHaveTheContractShape()
    {
        WindowActionArgs args = Parse<WindowActionArgs>("""{"windowId":"w1a2b-3c4","action":"minimize"}""");
        Assert.Equal("w1a2b-3c4", args.WindowId);
        Assert.Equal("minimize", args.Action);

        // "detail" is optional: present when there is something to say, absent otherwise.
        Assert.Equal(["succeeded"], Keys(Serialize(new WindowActionResult { Succeeded = true })));
        Assert.Equal(
            ["succeeded", "detail"],
            Keys(Serialize(new WindowActionResult { Succeeded = false, Detail = "Refused." })));
    }

    // ---------------------------------------------------------------------
    // Files
    // ---------------------------------------------------------------------

    [Fact]
    public void FileArgumentsReadTheContractFields()
    {
        Assert.Equal(@"C:\Users\me\Documents", Parse<FileListArgs>("""{"path":"C:\\Users\\me\\Documents"}""").Path);
        Assert.Null(Parse<FileListArgs>("{}").Path);

        Assert.Equal(@"C:\a\b.txt", Parse<FileOpenArgs>("""{"path":"C:\\a\\b.txt"}""").Path);

        FileCreateArgs create = Parse<FileCreateArgs>("""{"parent":"C:\\a","name":"New folder","isDirectory":true}""");
        Assert.Equal(@"C:\a", create.Parent);
        Assert.Equal("New folder", create.Name);
        Assert.True(create.IsDirectory);

        FileRenameArgs rename = Parse<FileRenameArgs>("""{"path":"C:\\a\\b.txt","newName":"c.txt"}""");
        Assert.Equal(@"C:\a\b.txt", rename.Path);
        Assert.Equal("c.txt", rename.NewName);

        FileTransferArgs transfer = Parse<FileTransferArgs>("""{"path":"C:\\a\\b.txt","destination":"D:\\backup"}""");
        Assert.Equal(@"C:\a\b.txt", transfer.Path);
        Assert.Equal(@"D:\backup", transfer.Destination);

        Assert.Null(Parse<FileDeleteArgs>("""{"path":"C:\\a\\b.txt"}""").Permanent);
        Assert.True(Parse<FileDeleteArgs>("""{"path":"C:\\a\\b.txt","permanent":true}""").Permanent);
        Assert.False(Parse<FileDeleteArgs>("""{"path":"C:\\a\\b.txt","permanent":false}""").Permanent);
    }

    [Fact]
    public void AFileEntryOmitsEveryOptionalFieldItDoesNotHave()
    {
        JsonObject file = Serialize(new FileEntry
        {
            Name = "report.pdf",
            Path = @"C:\Users\me\Documents\report.pdf",
            IsDirectory = false,
            Size = 123_456_789_012,
            Modified = "2026-10-12T08:30:00Z",
        });

        Assert.Equal(["name", "path", "isDirectory", "size", "modified"], Keys(file));
        Assert.Equal(123_456_789_012, file["size"]!.GetValue<long>());
        Assert.Equal("2026-10-12T08:30:00Z", file["modified"]!.GetValue<string>());

        // Backslashes are written as JSON requires and nothing else in a path is escaped.
        Assert.Contains("""C:\\Users\\me\\Documents\\report.pdf""", file.ToJsonString(ProtocolJson.Options), StringComparison.Ordinal);

        JsonObject folder = Serialize(new FileEntry { Name = "Photos", Path = @"D:\Photos", IsDirectory = true });
        Assert.Equal(["name", "path", "isDirectory"], Keys(folder));

        JsonObject root = Serialize(new FileEntry
        {
            Name = "Local Disk (C:)",
            Path = @"C:\",
            IsDirectory = true,
            IsRoot = true,
            ReadOnly = true,
        });

        Assert.Equal(["name", "path", "isDirectory", "isRoot", "readOnly"], Keys(root));
        Assert.True(root["isRoot"]!.GetValue<bool>());
        Assert.True(root["readOnly"]!.GetValue<bool>());
    }

    [Fact]
    public void AFileListResultHasTheContractShapeInBothViews()
    {
        JsonObject roots = Serialize(new FileListResult
        {
            Path = string.Empty,
            Entries = [new FileEntry { Name = "Desktop", Path = @"C:\Users\me\Desktop", IsDirectory = true, IsRoot = true }],
        });

        // The roots view: an empty path, and no parent key at all.
        Assert.Equal(["path", "entries", "truncated"], Keys(roots));
        Assert.Equal(string.Empty, roots["path"]!.GetValue<string>());
        Assert.False(roots["truncated"]!.GetValue<bool>());

        JsonObject directory = Serialize(new FileListResult
        {
            Path = @"C:\Users\me\Desktop\work",
            Parent = @"C:\Users\me\Desktop",
            Entries = [],
            Truncated = true,
        });

        Assert.Equal(["path", "parent", "entries", "truncated"], Keys(directory));
        Assert.Empty(directory["entries"]!.AsArray());

        FileListResult back = Parse<FileListResult>(directory.ToJsonString(ProtocolJson.Options));
        Assert.Equal(@"C:\Users\me\Desktop", back.Parent);
        Assert.True(back.Truncated);
    }

    [Fact]
    public void AnEntryResultWrapsTheEntryUnderEntry()
    {
        JsonObject json = Serialize(new FileEntryResult
        {
            Entry = new FileEntry { Name = "a.txt", Path = @"C:\a.txt", IsDirectory = false, Size = 0 },
        });

        Assert.Equal(["entry"], Keys(json));

        // A zero-byte file still reports its size: zero is a size, not an absent one.
        Assert.Equal(0, json["entry"]!["size"]!.GetValue<long>());
    }

    [Fact]
    public void AFileEntryRoundTripsThroughTheWireOptions()
    {
        var original = new FileEntry
        {
            Name = "名前 (2).txt",
            Path = @"C:\Users\me\名前 (2).txt",
            IsDirectory = false,
            Size = 42,
            Modified = "2026-01-02T03:04:05Z",
            ReadOnly = true,
        };

        FileEntry back = Parse<FileEntry>(JsonSerializer.Serialize(original, ProtocolJson.Options));

        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Path, back.Path);
        Assert.Equal(original.Size, back.Size);
        Assert.Equal(original.Modified, back.Modified);
        Assert.Null(back.IsRoot);
        Assert.True(back.ReadOnly);
    }

    // ---------------------------------------------------------------------
    // The pairing decision's compatibility default
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnApprovalServiceThatOnlyAnswersYesOrNoNeverGrantsFullControl(bool answer)
    {
        IPairingApprovalService legacy = new YesNoApproval(answer);

        PairingApprovalDecision decision = await legacy.RequestDecisionAsync(
            new PairingApprovalRequest("Phone", "android", "Pixel", "192.168.1.9", "AA:BB"),
            CancellationToken.None);

        Assert.Equal(answer, decision.Approved);
        Assert.False(decision.GrantFullControl);
    }

    private sealed class YesNoApproval(bool answer) : IPairingApprovalService
    {
        public bool CanPrompt => true;

        public Task<bool> RequestApprovalAsync(PairingApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(answer);
    }
}
