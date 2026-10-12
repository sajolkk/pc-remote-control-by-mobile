using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Core.Files;
using Xunit;
using FileOptions = RemoteAgent.Core.Configuration.FileOptions;

namespace RemoteAgent.Protocol.Tests;

/// <summary>
/// Tests for path canonicalisation, name validation and root containment.
/// </summary>
/// <remarks>
/// These are the attacks a file-management endpoint actually receives. Each case is a string an
/// attacker would send to reach something outside the folders the owner shared, and each must
/// either reduce to a canonical path that can be compared honestly, or be refused outright. None
/// of these tests touches a disk: the policy is purely lexical, which is what makes it possible to
/// test device paths and UNC shares at all.
/// </remarks>
public sealed class FilePathPolicyTests
{
    private static string Canonical(string input)
    {
        Assert.True(FilePathPolicy.TryCanonicalize(input, out string canonical, out PathRejection rejection), $"'{input}' was refused: {rejection}");
        Assert.Equal(PathRejection.None, rejection);
        return canonical;
    }

    private static PathRejection Rejected(string? input)
    {
        Assert.False(FilePathPolicy.TryCanonicalize(input, out string canonical, out PathRejection rejection), $"'{input}' was accepted as '{canonical}'.");
        Assert.Equal(string.Empty, canonical);
        Assert.NotEqual(PathRejection.None, rejection);
        return rejection;
    }

    // --- Canonical form ---

    [Theory]
    [InlineData(@"C:\Users\me\Documents", @"C:\Users\me\Documents")]
    [InlineData(@"c:\Users\me\Documents", @"C:\Users\me\Documents")]
    [InlineData(@"C:\Users\me\Documents\", @"C:\Users\me\Documents")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"d:\", @"D:\")]
    [InlineData(@"C:\\Users\\\me", @"C:\Users\me")]
    [InlineData(@"C:\Users\.\me\.", @"C:\Users\me")]
    [InlineData(@"C:\Users\me\file.tar.gz", @"C:\Users\me\file.tar.gz")]
    [InlineData(@"C:\Users\me\.gitignore", @"C:\Users\me\.gitignore")]
    [InlineData(@"C:\Users\me\My Documents (old)", @"C:\Users\me\My Documents (old)")]
    public void WellFormedPathsReduceToOneSpelling(string input, string expected)
    {
        Assert.Equal(expected, Canonical(input));
    }

    [Theory]
    [InlineData("C:/Users/me/Documents", @"C:\Users\me\Documents")]
    [InlineData(@"C:\Users/me\Documents/", @"C:\Users\me\Documents")]
    [InlineData("C:/Users\\me/../other", @"C:\Users\other")]
    public void ForwardAndMixedSeparatorsAreSeparators(string input, string expected)
    {
        Assert.Equal(expected, Canonical(input));
    }

    // --- Traversal ---

    [Theory]
    [InlineData(@"C:\Users\me\Documents\..\..\..\Windows\System32", @"C:\Windows\System32")]
    [InlineData(@"C:\Users\me\Documents\..", @"C:\Users\me")]
    [InlineData(@"C:\a\b\..\..", @"C:\")]
    [InlineData(@"C:\Users\me\Documents\sub\..\..\Documents2", @"C:\Users\me\Documents2")]
    public void DotDotIsResolvedBeforeAnythingIsCompared(string input, string expected)
    {
        Assert.Equal(expected, Canonical(input));
    }

    [Fact]
    public void TraversalOutOfARootIsCaughtByContainmentOnTheCanonicalPath()
    {
        const string root = @"C:\Users\me\Documents";

        // The text starts with the root. The file it names is not in it.
        string escaped = Canonical(@"C:\Users\me\Documents\..\..\..\Windows\System32\config\SAM");

        Assert.False(FilePathPolicy.IsWithin(escaped, root));
        Assert.True(FilePathPolicy.IsWithin(Canonical(@"C:\Users\me\Documents\a\..\b.txt"), root));
    }

    [Theory]
    [InlineData(@"C:\..")]
    [InlineData(@"C:\..\Windows")]
    [InlineData(@"C:\a\..\..\b")]
    [InlineData("C:/../../etc/passwd")]
    public void ClimbingAboveTheDriveRootIsRefusedNotClamped(string input)
    {
        Assert.Equal(PathRejection.Denied, Rejected(input));
    }

    // --- Device, extended-length and UNC paths ---

    [Theory]
    [InlineData(@"\\?\C:\Users\me\Documents")]
    [InlineData(@"\\?\C:\Windows\System32\config\SAM")]
    [InlineData(@"\\?\UNC\server\share\file.txt")]
    [InlineData(@"\\?\Volume{b75e2c83-0000-0000-0000-602f00000000}\secret.txt")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\secret.txt")]
    [InlineData(@"\\.\C:\Users\me")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\.\pipe\PCRemote.Agent")]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"\??\C:\Users\me")]
    [InlineData("//?/C:/Users/me")]
    [InlineData("//./C:/Users/me")]
    public void DeviceAndExtendedLengthPathsAreNeverServed(string input)
    {
        Assert.Equal(PathRejection.Denied, Rejected(input));
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\folder\file.txt")]
    [InlineData(@"\\192.168.1.9\c$\Users")]
    [InlineData(@"\\localhost\c$\Windows")]
    [InlineData("//server/share/file.txt")]
    [InlineData(@"\\attacker.example\x")]
    public void UncPathsAreNeverServed(string input)
    {
        Assert.Equal(PathRejection.Denied, Rejected(input));
    }

    // --- Alternate data streams ---

    [Theory]
    [InlineData(@"C:\Users\me\file.txt:stream")]
    [InlineData(@"C:\Users\me\file.txt:stream:$DATA")]
    [InlineData(@"C:\Users\me\file.txt::$DATA")]
    [InlineData(@"C:\Users\me\folder:$I30:$INDEX_ALLOCATION")]
    [InlineData(@"C:\Users\me:hidden\file.txt")]
    [InlineData(@"C:\Users\me\D:\other")]
    public void AColonAnywhereButAfterTheDriveLetterIsRefused(string input)
    {
        Assert.Equal(PathRejection.Denied, Rejected(input));
    }

    // --- Reserved device names ---

    [Theory]
    [InlineData(@"C:\Users\me\CON")]
    [InlineData(@"C:\Users\me\nul")]
    [InlineData(@"C:\Users\me\NUL.txt")]
    [InlineData(@"C:\Users\me\aux.tar.gz")]
    [InlineData(@"C:\Users\me\COM1")]
    [InlineData(@"C:\Users\me\lpt9.log")]
    [InlineData(@"C:\PRN\file.txt")]
    [InlineData(@"C:\Users\me\CONIN$")]
    public void ReservedDeviceNamesAreRefusedAsAnyComponent(string input)
    {
        Assert.Equal(PathRejection.Denied, Rejected(input));
    }

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("null")]
    [InlineData("auxiliary.txt")]
    [InlineData("LPT")]
    public void NamesThatOnlyResembleDeviceNamesAreFine(string name)
    {
        Assert.True(FilePathPolicy.IsValidName(name));
        Assert.Equal(@"C:\x\" + name, Canonical(@"C:\x\" + name));
    }

    // --- Trailing dots and spaces ---

    [Theory]
    [InlineData(@"C:\Users\me\secret.txt.")]
    [InlineData(@"C:\Users\me\secret.txt ")]
    [InlineData(@"C:\Users\me\secret.txt. .")]
    [InlineData(@"C:\Users\me.\Documents")]
    [InlineData(@"C:\Users\me \Documents")]
    [InlineData(@"C:\Users\me\...")]
    [InlineData(@"C:\Users\me\.. \x")]
    public void TrailingDotsAndSpacesAreRefusedBecauseWindowsWouldStripThem(string input)
    {
        Assert.Equal(PathRejection.Malformed, Rejected(input));
    }

    // --- Malformed and relative ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Documents")]
    [InlineData(@"Documents\file.txt")]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"C:")]
    [InlineData(@"C:file.txt")]
    [InlineData(@"C:..\other")]
    [InlineData(@"\Windows\System32")]
    [InlineData("/etc/passwd")]
    [InlineData(@"1:\file")]
    [InlineData(@"CC:\file")]
    [InlineData("%USERPROFILE%\\Documents")]
    [InlineData("~\\Documents")]
    public void AnythingThatIsNotADriveAbsolutePathIsMalformed(string? input)
    {
        Assert.Equal(PathRejection.Malformed, Rejected(input));
    }

    [Fact]
    public void ControlCharactersAreRefused()
    {
        // A loop rather than a theory: these strings contain NUL and newline, which do not survive
        // being used as test-case names. "file.txt\0.jpg" is the classic null-byte truncation.
        string[] inputs =
        [
            "C:\\Users\\me\\a\0b",
            "C:\\Users\\me\\a\nb",
            "C:\\Users\\me\\a\tb",
            "C:\\Users\\me\\a\rb",
            "C:\\Users\\me\\file.txt\0.jpg",
            "C:\\Users\\me\\a\u001Fb",
            "C:\\Users\\me\\a\u007Fb",
        ];

        foreach (string input in inputs)
        {
            Assert.Equal(PathRejection.Malformed, Rejected(input));
        }
    }

    [Theory]
    [InlineData(@"C:\Users\me\a*b")]
    [InlineData(@"C:\Users\me\a?b")]
    [InlineData(@"C:\Users\me\a<b")]
    [InlineData(@"C:\Users\me\a>b")]
    [InlineData(@"C:\Users\me\a|b")]
    [InlineData("C:\\Users\\me\\a\"b")]
    public void WildcardsAndRedirectionCharactersAreRefused(string input)
    {
        Assert.Equal(PathRejection.Malformed, Rejected(input));
    }

    [Fact]
    public void AnAbsurdlyLongPathIsRefused()
    {
        string path = @"C:\" + string.Join('\\', Enumerable.Repeat("segment", 6000));

        Assert.Equal(PathRejection.Malformed, Rejected(path));
    }

    // --- Root containment ---

    [Theory]
    [InlineData(@"C:\Users\me", @"C:\Users\me", true)]
    [InlineData(@"C:\Users\me\Documents", @"C:\Users\me", true)]
    [InlineData(@"C:\Users\me\Documents\a.txt", @"C:\Users\me", true)]
    [InlineData(@"C:\USERS\ME\documents", @"C:\Users\me", true)]
    [InlineData(@"C:\Users\meow", @"C:\Users\me", false)]
    [InlineData(@"C:\Users\meow\file.txt", @"C:\Users\me", false)]
    [InlineData(@"C:\Users\me2", @"C:\Users\me", false)]
    [InlineData(@"C:\Users\me.bak\x", @"C:\Users\me", false)]
    [InlineData(@"C:\Users", @"C:\Users\me", false)]
    [InlineData(@"C:\", @"C:\Users\me", false)]
    [InlineData(@"D:\Users\me", @"C:\Users\me", false)]
    public void ContainmentIsDecidedByComponentsNotByPrefix(string path, string root, bool expected)
    {
        Assert.Equal(expected, FilePathPolicy.IsWithin(path, root));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"C:\a\b\c.txt", true)]
    [InlineData(@"D:\", false)]
    [InlineData(@"D:\Windows", false)]
    public void ADriveRootContainsThatDriveAndNoOther(string path, bool expected)
    {
        Assert.Equal(expected, FilePathPolicy.IsWithin(path, @"C:\"));
    }

    // --- Names ---

    [Theory]
    [InlineData("report.docx")]
    [InlineData("New folder")]
    [InlineData(".gitignore")]
    [InlineData("a")]
    [InlineData("file (2).txt")]
    [InlineData("résumé — final.pdf")]
    [InlineData("名前.txt")]
    [InlineData("  leading spaces are legal")]
    [InlineData("has.many.dots.txt")]
    public void OrdinaryNamesAreValid(string name)
    {
        Assert.True(FilePathPolicy.IsValidName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData(@"..\escape.txt")]
    [InlineData("../escape.txt")]
    [InlineData(@"sub\..\..\escape.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("file.txt:stream")]
    [InlineData("C:")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("NUL.txt")]
    [InlineData("com1.log")]
    [InlineData("LPT1")]
    public void NamesThatAreNotASingleSafeComponentAreInvalid(string? name)
    {
        Assert.False(FilePathPolicy.IsValidName(name));
    }

    [Fact]
    public void ANameContainingAControlCharacterIsInvalid()
    {
        foreach (string name in new[] { "a\0b", "a\nb", "a\tb", "a\u001Fb", "report.txt\0.exe" })
        {
            Assert.False(FilePathPolicy.IsValidName(name));
        }
    }

    [Fact]
    public void ANameLongerThanNtfsAllowsIsInvalid()
    {
        Assert.True(FilePathPolicy.IsValidName(new string('a', 255)));
        Assert.False(FilePathPolicy.IsValidName(new string('a', 256)));
    }

    // --- Parent, name, combine ---

    [Theory]
    [InlineData(@"C:\a\b\c.txt", @"C:\a\b")]
    [InlineData(@"C:\a", @"C:\")]
    public void ParentOfAPath(string path, string expected)
    {
        Assert.Equal(expected, FilePathPolicy.GetParent(path));
    }

    [Fact]
    public void ADriveRootHasNoParent()
    {
        Assert.Null(FilePathPolicy.GetParent(@"C:\"));
    }

    [Fact]
    public void NameAndCombineRoundTrip()
    {
        Assert.Equal("c.txt", FilePathPolicy.GetName(@"C:\a\b\c.txt"));
        Assert.Equal(@"C:\", FilePathPolicy.GetName(@"C:\"));
        Assert.Equal(@"C:\a\b", FilePathPolicy.Combine(@"C:\a", "b"));
        Assert.Equal(@"C:\b", FilePathPolicy.Combine(@"C:\", "b"));
    }

    // --- Roots from configuration ---

    private sealed class FixedDefaults(params FileRoot[] roots) : IDefaultFileRoots
    {
        public IReadOnlyList<FileRoot> GetDefaultRoots() => roots;
    }

    private static readonly FixedDefaults Defaults = new(
        new FileRoot("Documents", @"C:\Users\me\Documents", true),
        new FileRoot("Local Disk (C:)", @"C:\", true));

    private static string Expand(string value) => value.Replace("%USERPROFILE%", @"C:\Users\me", StringComparison.Ordinal);

    [Fact]
    public void ByDefaultTheBuiltInRootsAreOfferedWritable()
    {
        var roots = new ConfiguredFileRoots(() => new FileOptions(), Defaults, Expand);

        IReadOnlyList<FileRoot> result = roots.GetRoots();

        Assert.Equal([@"C:\Users\me\Documents", @"C:\"], result.Select(r => r.Path));
        Assert.All(result, root => Assert.True(root.Writable));
    }

    [Fact]
    public void ConfigurationCanNarrowToExactlyTheListedRoots()
    {
        var options = new FileOptions
        {
            UseDefaultRoots = false,
            AllowedRoots = [new AllowedRoot { Alias = "Inbox", Path = @"%USERPROFILE%\Downloads\", Write = false }],
        };

        FileRoot only = Assert.Single(new ConfiguredFileRoots(() => options, Defaults, Expand).GetRoots());

        Assert.Equal(new FileRoot("Inbox", @"C:\Users\me\Downloads", false), only);
    }

    [Fact]
    public void NarrowingToNothingLeavesNothing()
    {
        var options = new FileOptions { UseDefaultRoots = false };

        Assert.Empty(new ConfiguredFileRoots(() => options, Defaults, Expand).GetRoots());
    }

    [Fact]
    public void AConfiguredEntryReplacesTheBuiltInRootForTheSameFolder()
    {
        // How an owner keeps the whole drive browsable but read-only.
        var options = new FileOptions
        {
            AllowedRoots = [new AllowedRoot { Alias = "C drive", Path = @"c:\", Write = false }],
        };

        IReadOnlyList<FileRoot> roots = new ConfiguredFileRoots(() => options, Defaults, Expand).GetRoots();

        Assert.Equal(2, roots.Count);
        Assert.Contains(new FileRoot("C drive", @"C:\", false), roots);
        Assert.Contains(new FileRoot("Documents", @"C:\Users\me\Documents", true), roots);
    }

    [Fact]
    public void AnUnknownPolicyAllowsNothing()
    {
        // The session agent has not heard from the service yet. The built-in roots are the whole
        // PC, so they must not be what "unknown" falls back to.
        Assert.Empty(new ConfiguredFileRoots(() => null, Defaults, Expand).GetRoots());
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\")]
    [InlineData(@"relative\folder")]
    [InlineData(@"%NOT_A_VARIABLE%\Documents")]
    [InlineData(@"C:\ok\..\..\up")]
    [InlineData("")]
    [InlineData("   ")]
    public void AConfiguredRootThatIsNotAPlainDrivePathIsIgnored(string path)
    {
        var options = new FileOptions
        {
            UseDefaultRoots = false,
            AllowedRoots = [new AllowedRoot { Alias = "Bad", Path = path, Write = true }],
        };

        Assert.Empty(new ConfiguredFileRoots(() => options, Defaults, Expand).GetRoots());
    }

    [Fact]
    public void TheMostSpecificRootGovernsAPath()
    {
        FileRoot[] roots =
        [
            new("Local Disk (C:)", @"C:\", true),
            new("Documents", @"C:\Users\me\Documents", false),
        ];

        Assert.Equal("Documents", ConfiguredFileRoots.FindGoverningRoot(@"C:\Users\me\Documents\a.txt", roots)!.Label);
        Assert.Equal("Local Disk (C:)", ConfiguredFileRoots.FindGoverningRoot(@"C:\Users\me\Documents2\a.txt", roots)!.Label);
        Assert.Null(ConfiguredFileRoots.FindGoverningRoot(@"D:\a.txt", roots));
    }

    [Fact]
    public void TheListEarlierVersionsGeneratedIsRecognisedAndAnEditedOneIsNot()
    {
        AllowedRoot[] Generated() =>
        [
            new() { Alias = "Downloads", Path = "%USERPROFILE%\\Downloads", Write = true },
            new() { Alias = "Documents", Path = "%USERPROFILE%\\Documents", Write = false },
            new() { Alias = "Desktop", Path = "%USERPROFILE%\\Desktop", Write = false },
        ];

        Assert.True(new FileOptions { AllowedRoots = Generated() }.HasLegacyGeneratedRoots());
        Assert.False(new FileOptions().HasLegacyGeneratedRoots());

        // Any sign of a human: a changed flag, a removed entry, an added one.
        AllowedRoot[] flagChanged = Generated();
        flagChanged[1].Write = true;
        Assert.False(new FileOptions { AllowedRoots = flagChanged }.HasLegacyGeneratedRoots());

        Assert.False(new FileOptions { AllowedRoots = Generated()[..2] }.HasLegacyGeneratedRoots());
        Assert.False(new FileOptions
        {
            AllowedRoots = [.. Generated(), new AllowedRoot { Alias = "Work", Path = @"D:\Work" }],
        }.HasLegacyGeneratedRoots());
    }
}
