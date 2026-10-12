using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Files;
using RemoteAgent.Protocol.Messages;
using Xunit;

namespace RemoteAgent.Protocol.Tests;

/// <summary>
/// Tests for file operations, against a throwaway directory tree.
/// </summary>
/// <remarks>
/// <para>Every file these tests create, change or delete lives under one freshly made directory in
/// the system temp folder, removed again when the test ends. The allowed roots handed to the
/// <see cref="FileManager"/> are subdirectories of it, and a sibling directory that is deliberately
/// <em>not</em> a root plays the part of "the rest of the PC" — so an escape attempt that worked
/// would damage a decoy, and the assertion that the decoy is intact is the test.</para>
///
/// <para>The Windows shell is replaced by <see cref="FakeShell"/>. Nothing is sent to the real
/// Recycle Bin and nothing is opened on the desktop these tests run on.</para>
/// </remarks>
public sealed class FileManagerTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly string _readOnlyRoot;
    private readonly string _outside;
    private readonly FakeShell _shell = new();
    private readonly FixedRoots _roots;
    private readonly FileManager _files;

    public FileManagerTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "pcremote-tests", "files-" + Guid.NewGuid().ToString("n"));
        _root = Directory.CreateDirectory(Path.Combine(_base, "shared")).FullName;
        _readOnlyRoot = Directory.CreateDirectory(Path.Combine(_base, "readonly")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;

        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "not shared");
        File.WriteAllText(Path.Combine(_readOnlyRoot, "fixed.txt"), "read only");

        _roots = new FixedRoots(
            new FileRoot("Shared", Canonical(_root), Writable: true),
            new FileRoot("Reference", Canonical(_readOnlyRoot), Writable: false));

        _files = new FileManager(_roots, _shell, NullLogger<FileManager>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_base))
            {
                // Read-only attributes set by a test would otherwise make the cleanup fail.
                foreach (string file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                DeleteTree(_base);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp directory that could not be removed is litter, not a test failure.
        }
    }

    /// <summary>
    /// Removes the test tree, including one that contains a junction.
    /// </summary>
    /// <remarks>
    /// A recursive delete removes a junction it meets (the link, never its target) but then
    /// reports an error for it and leaves the junction's parent directories behind. By then the
    /// link is gone, so a second pass has nothing to object to.
    /// </remarks>
    private static void DeleteTree(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string Canonical(string path)
    {
        Assert.True(FilePathPolicy.TryCanonicalize(path, out string canonical, out _), $"The temp path '{path}' is not canonicalisable.");
        return canonical;
    }

    private string InRoot(params string[] parts) => Path.Combine([_root, .. parts]);

    private string WriteFile(string relative, string content = "content")
    {
        string path = InRoot(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void AssertDecoyIntact()
    {
        Assert.Equal("not shared", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
        Assert.Single(Directory.GetFileSystemEntries(_outside));
    }

    // ---------------------------------------------------------------------
    // file.list
    // ---------------------------------------------------------------------

    [Fact]
    public void TheRootsViewListsEveryRootWithItsLabel()
    {
        FileResult<FileListResult> result = _files.List(null);

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.Value!.Path);
        Assert.Null(result.Value.Parent);
        Assert.False(result.Value.Truncated);

        Assert.Equal(["Shared", "Reference"], result.Value.Entries.Select(e => e.Name));
        Assert.All(result.Value.Entries, entry =>
        {
            Assert.True(entry.IsDirectory);
            Assert.True(entry.IsRoot);
            Assert.Null(entry.Size);
        });

        Assert.Null(result.Value.Entries[0].ReadOnly);
        Assert.True(result.Value.Entries[1].ReadOnly);

        // An empty string means the same as an absent path.
        Assert.Equal(2, _files.List(string.Empty).Value!.Entries.Length);
    }

    [Fact]
    public void ARootThatDoesNotExistIsNotOffered()
    {
        _roots.Roots.Add(new FileRoot("Gone", Canonical(Path.Combine(_base, "no-such-folder")), true));

        Assert.DoesNotContain(_files.List(null).Value!.Entries, entry => entry.Name == "Gone");
    }

    [Fact]
    public void ListingPutsDirectoriesFirstThenFilesEachAlphabetical()
    {
        WriteFile("zebra.txt");
        WriteFile("Apple.txt", "12345");
        WriteFile("banana.txt");
        Directory.CreateDirectory(InRoot("Zoo"));
        Directory.CreateDirectory(InRoot("archive"));

        FileResult<FileListResult> result = _files.List(_root);

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["archive", "Zoo", "Apple.txt", "banana.txt", "zebra.txt"],
            result.Value!.Entries.Select(e => e.Name));

        FileEntry apple = result.Value.Entries[2];
        Assert.False(apple.IsDirectory);
        Assert.Equal(5, apple.Size);
        Assert.Equal(Canonical(InRoot("Apple.txt")), apple.Path);
        Assert.Null(apple.IsRoot);
        Assert.Null(apple.ReadOnly);

        // ISO-8601 UTC, to the second.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", apple.Modified!);
        Assert.True(DateTimeOffset.Parse(apple.Modified!) > DateTimeOffset.UtcNow.AddMinutes(-10));

        FileEntry archive = result.Value.Entries[0];
        Assert.True(archive.IsDirectory);
        Assert.Null(archive.Size);
    }

    [Fact]
    public void ListingReportsTheParentBelowARootAndNoneAtARoot()
    {
        Directory.CreateDirectory(InRoot("a", "b"));

        Assert.Null(_files.List(_root).Value!.Parent);
        Assert.Equal(Canonical(_root), _files.List(InRoot("a")).Value!.Parent);
        Assert.Equal(Canonical(InRoot("a")), _files.List(InRoot("a", "b")).Value!.Parent);
    }

    [Fact]
    public void ListingAcceptsEquivalentSpellingsAndAnswersWithTheCanonicalOne()
    {
        Directory.CreateDirectory(InRoot("docs", "inner"));

        string messy = _root.Replace('\\', '/') + "/docs/inner/../";

        FileResult<FileListResult> result = _files.List(messy);

        Assert.True(result.Succeeded);
        Assert.Equal(Canonical(InRoot("docs")), result.Value!.Path);
        Assert.Equal("inner", Assert.Single(result.Value.Entries).Name);
    }

    [Fact]
    public void ListingMarksEntriesOfAReadOnlyRootAndReadOnlyFiles()
    {
        string locked = WriteFile("locked.txt");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        WriteFile("free.txt");

        FileEntry[] shared = _files.List(_root).Value!.Entries;
        Assert.Null(shared.Single(e => e.Name == "free.txt").ReadOnly);
        Assert.True(shared.Single(e => e.Name == "locked.txt").ReadOnly);

        Assert.True(_files.List(_readOnlyRoot).Value!.Entries.Single().ReadOnly);
    }

    [Fact]
    public void ListingIsCappedAndSaysSo()
    {
        string many = Directory.CreateDirectory(InRoot("many")).FullName;
        for (int i = 0; i < FileManager.MaxListEntries + 25; i++)
        {
            File.WriteAllBytes(Path.Combine(many, $"f{i:D5}.txt"), []);
        }

        FileListResult result = _files.List(many).Value!;

        Assert.Equal(FileManager.MaxListEntries, result.Entries.Length);
        Assert.True(result.Truncated);
        Assert.Equal("f00000.txt", result.Entries[0].Name);
    }

    [Fact]
    public void ListingAMissingFolderIsNotFoundAndAFileIsInvalid()
    {
        WriteFile("plain.txt");

        Assert.Equal(FileStatus.NotFound, _files.List(InRoot("nope")).Status);
        Assert.Equal(FileStatus.InvalidArguments, _files.List(InRoot("plain.txt")).Status);
    }

    // ---------------------------------------------------------------------
    // Staying inside the roots
    // ---------------------------------------------------------------------

    [Fact]
    public void NothingOutsideTheRootsCanBeListedOpenedOrTouched()
    {
        string secret = Path.Combine(_outside, "secret.txt");
        string traversal = InRoot("..", "outside", "secret.txt");
        string traversalDirectory = InRoot("..", "outside");

        Assert.Equal(FileStatus.AccessDenied, _files.List(_outside).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.List(traversalDirectory).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.List(_base).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.List(@"C:\").Status);

        Assert.Equal(FileStatus.AccessDenied, _files.Open(secret, mayLaunchPrograms: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Open(traversal, mayLaunchPrograms: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Delete(secret, permanent: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Delete(traversal, permanent: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Delete(traversalDirectory, permanent: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Rename(secret, "stolen.txt").Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Create(_outside, "planted.txt", isDirectory: false).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Create(traversalDirectory, "planted.txt", isDirectory: false).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Copy(secret, _root, CancellationToken.None).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Move(secret, _root, CancellationToken.None).Status);

        WriteFile("mine.txt");
        Assert.Equal(FileStatus.AccessDenied, _files.Copy(InRoot("mine.txt"), _outside, CancellationToken.None).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Move(InRoot("mine.txt"), traversalDirectory, CancellationToken.None).Status);

        AssertDecoyIntact();
        Assert.Empty(Directory.GetFiles(_root, "secret.txt"));
        Assert.True(File.Exists(InRoot("mine.txt")));
        Assert.Empty(_shell.Opened);
        Assert.Empty(_shell.Recycled);
    }

    [Fact]
    public void ASiblingWhoseNameStartsWithTheRootsNameIsOutside()
    {
        // The root is ...\shared. ...\shared-private is a different folder.
        string lookalike = Directory.CreateDirectory(_root + "-private").FullName;
        File.WriteAllText(Path.Combine(lookalike, "diary.txt"), "private");

        Assert.Equal(FileStatus.AccessDenied, _files.List(lookalike).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Delete(Path.Combine(lookalike, "diary.txt"), permanent: true).Status);
        Assert.True(File.Exists(Path.Combine(lookalike, "diary.txt")));
    }

    [Theory]
    [InlineData(@"\\?\{0}")]
    [InlineData(@"\\.\{0}")]
    [InlineData(@"\\localhost\c$\Windows")]
    [InlineData(@"{0}\file.txt:stream")]
    [InlineData(@"{0}\NUL")]
    [InlineData(@"{0}\..\..\..\..\..\..\..\..\..\..\..\..\..\..\..\..\Windows")]
    public void HostilePathFormsAreDeniedEvenWhenTheyNameTheRoot(string template)
    {
        string path = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, _root);

        Assert.Equal(FileStatus.AccessDenied, _files.List(path).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Delete(path, permanent: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Open(path, mayLaunchPrograms: true).Status);
    }

    [Theory]
    [InlineData("relative.txt")]
    [InlineData(@"..\outside\secret.txt")]
    [InlineData("C:relative.txt")]
    [InlineData("")]
    public void RelativePathsAreInvalidWhereAPathIsRequired(string path)
    {
        Assert.Equal(FileStatus.InvalidArguments, _files.Delete(path, permanent: true).Status);
        Assert.Equal(FileStatus.InvalidArguments, _files.Open(path, mayLaunchPrograms: true).Status);
        Assert.Equal(FileStatus.InvalidArguments, _files.Create(path, "x.txt", isDirectory: false).Status);
        AssertDecoyIntact();
    }

    [Fact]
    public void AJunctionOutOfTheRootsCannotBeFollowed()
    {
        string link = InRoot("shortcut-out");
        if (!TryCreateJunction(link, _outside))
        {
            // Neither a symbolic link nor a junction could be created on this machine.
            return;
        }

        // Lexically every one of these paths is inside the root. Physically none is.
        string through = Path.Combine(link, "secret.txt");

        Assert.Equal(FileStatus.AccessDenied, _files.List(link).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Open(through, mayLaunchPrograms: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Delete(through, permanent: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Rename(through, "moved.txt").Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Create(link, "planted.txt", isDirectory: false).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Copy(through, _root, CancellationToken.None).Status);

        AssertDecoyIntact();
    }

    [Fact]
    public void AJunctionThatStaysInsideTheRootsWorks()
    {
        string target = Directory.CreateDirectory(InRoot("real")).FullName;
        File.WriteAllText(Path.Combine(target, "inside.txt"), "ok");

        string link = InRoot("alias");
        if (!TryCreateJunction(link, target))
        {
            return;
        }

        Assert.Equal("inside.txt", Assert.Single(_files.List(link).Value!.Entries).Name);
    }

    [Fact]
    public void CopyingAFolderDoesNotFollowLinksInsideIt()
    {
        string source = Directory.CreateDirectory(InRoot("album")).FullName;
        File.WriteAllText(Path.Combine(source, "photo.jpg"), "pixels");

        if (!TryCreateJunction(Path.Combine(source, "elsewhere"), _outside))
        {
            return;
        }

        string destination = Directory.CreateDirectory(InRoot("backup")).FullName;

        FileResult<FileEntry> result = _files.Copy(source, destination, CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.True(File.Exists(Path.Combine(destination, "album", "photo.jpg")));
        Assert.False(Directory.Exists(Path.Combine(destination, "album", "elsewhere")));
        Assert.Empty(Directory.GetFiles(destination, "secret.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void PermanentlyDeletingAFolderRemovesALinkInsideItButNeverWhatTheLinkPointsAt()
    {
        string folder = Directory.CreateDirectory(InRoot("to-delete", "nested")).FullName;
        File.WriteAllText(Path.Combine(folder, "mine.txt"), "disposable");

        if (!TryCreateJunction(Path.Combine(folder, "way-out"), _outside))
        {
            return;
        }

        FileResult<bool> result = _files.Delete(InRoot("to-delete"), permanent: true);

        Assert.True(result.Succeeded, result.Detail);
        Assert.False(Directory.Exists(InRoot("to-delete")));

        // The junction was removed as a link. The folder it led to still has its file.
        AssertDecoyIntact();
    }

    // ---------------------------------------------------------------------
    // Read-only roots, and roots protecting themselves
    // ---------------------------------------------------------------------

    [Fact]
    public void AReadOnlyRootCanBeReadButNotChanged()
    {
        string fixedFile = Path.Combine(_readOnlyRoot, "fixed.txt");

        Assert.True(_files.List(_readOnlyRoot).Succeeded);
        Assert.True(_files.Open(fixedFile, mayLaunchPrograms: false).Succeeded);

        Assert.Equal(FileStatus.AccessDenied, _files.Create(_readOnlyRoot, "new.txt", isDirectory: false).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Create(_readOnlyRoot, "new", isDirectory: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Rename(fixedFile, "renamed.txt").Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Delete(fixedFile, permanent: false).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Delete(fixedFile, permanent: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Move(fixedFile, _root, CancellationToken.None).Status);

        WriteFile("incoming.txt");
        Assert.Equal(FileStatus.AccessDenied, _files.Copy(InRoot("incoming.txt"), _readOnlyRoot, CancellationToken.None).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Move(InRoot("incoming.txt"), _readOnlyRoot, CancellationToken.None).Status);

        Assert.Equal(["fixed.txt"], Directory.GetFileSystemEntries(_readOnlyRoot).Select(Path.GetFileName));
        Assert.Equal("read only", File.ReadAllText(fixedFile));
        Assert.Empty(_shell.Recycled);

        // Copying OUT of a read-only root only reads it, and is allowed.
        Assert.True(_files.Copy(fixedFile, _root, CancellationToken.None).Succeeded);
        Assert.True(File.Exists(InRoot("fixed.txt")));
    }

    [Fact]
    public void ARootItselfCannotBeRenamedMovedOrDeleted()
    {
        string elsewhere = Directory.CreateDirectory(InRoot("elsewhere")).FullName;

        Assert.Equal(FileStatus.AccessDenied, _files.Rename(_root, "renamed").Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Delete(_root, permanent: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Delete(_root, permanent: false).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Move(_root, elsewhere, CancellationToken.None).Status);

        Assert.True(Directory.Exists(_root));
        Assert.Empty(_shell.Recycled);
    }

    [Fact]
    public void AFolderThatContainsARootIsProtectedToo()
    {
        // The whole base directory becomes a writable root; "shared" and "readonly" are still
        // roots inside it. Deleting the base would take them with it.
        _roots.Roots.Add(new FileRoot("Everything", Canonical(_base), true));
        string holder = Directory.CreateDirectory(Path.Combine(_base, "holder")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(holder, "nested-root")).FullName;
        _roots.Roots.Add(new FileRoot("Nested", Canonical(nested), true));

        Assert.Equal(FileStatus.AccessDenied, _files.Delete(holder, permanent: true).Status);
        Assert.Equal(FileStatus.AccessDenied, _files.Rename(holder, "holder2").Status);
        Assert.True(Directory.Exists(nested));

        // An ordinary sibling of the roots is not protected.
        Assert.True(_files.Delete(_outside, permanent: true).Succeeded);
        Assert.False(Directory.Exists(_outside));
    }

    // ---------------------------------------------------------------------
    // file.create
    // ---------------------------------------------------------------------

    [Fact]
    public void CreateMakesAnEmptyFileOrAFolder()
    {
        FileResult<FileEntry> file = _files.Create(_root, "notes.txt", isDirectory: false);
        FileResult<FileEntry> folder = _files.Create(_root, "New folder", isDirectory: true);

        Assert.True(file.Succeeded, file.Detail);
        Assert.Equal("notes.txt", file.Value!.Name);
        Assert.Equal(Canonical(InRoot("notes.txt")), file.Value.Path);
        Assert.False(file.Value.IsDirectory);
        Assert.Equal(0, file.Value.Size);
        Assert.Equal(0, new FileInfo(InRoot("notes.txt")).Length);

        Assert.True(folder.Succeeded, folder.Detail);
        Assert.True(folder.Value!.IsDirectory);
        Assert.Null(folder.Value.Size);
        Assert.True(Directory.Exists(InRoot("New folder")));
    }

    [Fact]
    public void CreateNeverOverwrites()
    {
        string existing = WriteFile("keep.txt", "precious");
        Directory.CreateDirectory(InRoot("taken"));

        Assert.Equal(FileStatus.InvalidArguments, _files.Create(_root, "keep.txt", isDirectory: false).Status);
        Assert.Equal(FileStatus.InvalidArguments, _files.Create(_root, "KEEP.TXT", isDirectory: false).Status);
        Assert.Equal(FileStatus.InvalidArguments, _files.Create(_root, "keep.txt", isDirectory: true).Status);
        Assert.Equal(FileStatus.InvalidArguments, _files.Create(_root, "taken", isDirectory: false).Status);

        Assert.Equal("precious", File.ReadAllText(existing));
        Assert.True(Directory.Exists(InRoot("taken")));
    }

    [Theory]
    [InlineData(@"..\escape.txt")]
    [InlineData("../escape.txt")]
    [InlineData(@"sub\file.txt")]
    [InlineData("sub/file.txt")]
    [InlineData(@"..\outside\planted.txt")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("file.txt:stream")]
    [InlineData("NUL")]
    [InlineData("con.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("what?.txt")]
    [InlineData("a*b")]
    public void ANameMustBeASingleSafeComponent(string name)
    {
        string victim = WriteFile("victim.txt", "original");

        Assert.Equal(FileStatus.InvalidArguments, _files.Create(_root, name, isDirectory: false).Status);
        Assert.Equal(FileStatus.InvalidArguments, _files.Create(_root, name, isDirectory: true).Status);
        Assert.Equal(FileStatus.InvalidArguments, _files.Rename(victim, name).Status);

        Assert.Equal("original", File.ReadAllText(victim));
        Assert.Equal(["victim.txt"], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
        AssertDecoyIntact();
    }

    [Fact]
    public void CreateInAMissingParentIsNotFound()
    {
        Assert.Equal(FileStatus.NotFound, _files.Create(InRoot("nowhere"), "a.txt", isDirectory: false).Status);
        Assert.False(Directory.Exists(InRoot("nowhere")));
    }

    // ---------------------------------------------------------------------
    // file.rename
    // ---------------------------------------------------------------------

    [Fact]
    public void RenameChangesTheNameInPlace()
    {
        string file = WriteFile("sub/draft.txt", "words");
        Directory.CreateDirectory(InRoot("old-folder"));

        FileResult<FileEntry> renamedFile = _files.Rename(file, "final.txt");
        FileResult<FileEntry> renamedFolder = _files.Rename(InRoot("old-folder"), "new-folder");

        Assert.True(renamedFile.Succeeded, renamedFile.Detail);
        Assert.Equal(Canonical(InRoot("sub", "final.txt")), renamedFile.Value!.Path);
        Assert.Equal("words", File.ReadAllText(InRoot("sub", "final.txt")));
        Assert.False(File.Exists(file));

        Assert.True(renamedFolder.Succeeded, renamedFolder.Detail);
        Assert.True(renamedFolder.Value!.IsDirectory);
        Assert.True(Directory.Exists(InRoot("new-folder")));
    }

    [Fact]
    public void RenameNeverOverwrites()
    {
        string a = WriteFile("a.txt", "A");
        string b = WriteFile("b.txt", "B");

        Assert.Equal(FileStatus.InvalidArguments, _files.Rename(a, "b.txt").Status);

        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
    }

    [Fact]
    public void RenameCanChangeOnlyTheLetterCase()
    {
        string file = WriteFile("readme.txt");

        FileResult<FileEntry> result = _files.Rename(file, "README.txt");

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("README.txt", result.Value!.Name);
        Assert.Equal(["README.txt"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void RenameOfSomethingMissingIsNotFound()
    {
        Assert.Equal(FileStatus.NotFound, _files.Rename(InRoot("ghost.txt"), "real.txt").Status);
    }

    // ---------------------------------------------------------------------
    // file.copy
    // ---------------------------------------------------------------------

    [Fact]
    public void CopyPutsAFileInTheDestinationFolderAndKeepsTheOriginal()
    {
        string source = WriteFile("report.pdf", "data");
        string destination = Directory.CreateDirectory(InRoot("archive")).FullName;

        FileResult<FileEntry> result = _files.Copy(source, destination, CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(Canonical(Path.Combine(destination, "report.pdf")), result.Value!.Path);
        Assert.Equal(4, result.Value.Size);
        Assert.Equal("data", File.ReadAllText(Path.Combine(destination, "report.pdf")));
        Assert.Equal("data", File.ReadAllText(source));
        Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(Path.Combine(destination, "report.pdf")));
    }

    [Fact]
    public void CopyPicksANumberedNameInsteadOfOverwriting()
    {
        string source = WriteFile("photo.jpg", "new");
        string destination = Directory.CreateDirectory(InRoot("album")).FullName;
        File.WriteAllText(Path.Combine(destination, "photo.jpg"), "old");

        FileResult<FileEntry> second = _files.Copy(source, destination, CancellationToken.None);
        FileResult<FileEntry> third = _files.Copy(source, destination, CancellationToken.None);

        Assert.Equal("photo (2).jpg", second.Value!.Name);
        Assert.Equal("photo (3).jpg", third.Value!.Name);
        Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "photo.jpg")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "photo (2).jpg")));
    }

    [Fact]
    public void CopyingIntoTheSameFolderDuplicatesUnderANumberedName()
    {
        string source = WriteFile("budget.xlsx", "numbers");
        WriteFile(".gitignore", "bin/");
        WriteFile("LICENSE", "MIT");

        Assert.Equal("budget (2).xlsx", _files.Copy(source, _root, CancellationToken.None).Value!.Name);

        // A leading dot is part of the name, and a name with no dot simply gets the suffix.
        Assert.Equal(".gitignore (2)", _files.Copy(InRoot(".gitignore"), _root, CancellationToken.None).Value!.Name);
        Assert.Equal("LICENSE (2)", _files.Copy(InRoot("LICENSE"), _root, CancellationToken.None).Value!.Name);
    }

    [Fact]
    public void CopyDuplicatesAFolderTree()
    {
        WriteFile("project/src/main.cs", "code");
        WriteFile("project/readme.md", "docs");
        Directory.CreateDirectory(InRoot("project", "empty"));
        string destination = Directory.CreateDirectory(InRoot("backups")).FullName;

        FileResult<FileEntry> first = _files.Copy(InRoot("project"), destination, CancellationToken.None);
        FileResult<FileEntry> second = _files.Copy(InRoot("project"), destination, CancellationToken.None);

        Assert.True(first.Succeeded, first.Detail);
        Assert.True(first.Value!.IsDirectory);
        Assert.Equal("code", File.ReadAllText(Path.Combine(destination, "project", "src", "main.cs")));
        Assert.Equal("docs", File.ReadAllText(Path.Combine(destination, "project", "readme.md")));
        Assert.True(Directory.Exists(Path.Combine(destination, "project", "empty")));

        Assert.Equal("project (2)", second.Value!.Name);
        Assert.Equal("code", File.ReadAllText(Path.Combine(destination, "project (2)", "src", "main.cs")));
    }

    [Fact]
    public void AFolderCannotBeCopiedOrMovedIntoItself()
    {
        WriteFile("loop/inner/file.txt");

        Assert.Equal(FileStatus.InvalidArguments, _files.Copy(InRoot("loop"), InRoot("loop", "inner"), CancellationToken.None).Status);
        Assert.Equal(FileStatus.InvalidArguments, _files.Copy(InRoot("loop"), InRoot("loop"), CancellationToken.None).Status);
        Assert.Equal(FileStatus.InvalidArguments, _files.Move(InRoot("loop"), InRoot("loop", "inner"), CancellationToken.None).Status);

        Assert.Equal(["inner"], Directory.GetFileSystemEntries(InRoot("loop")).Select(Path.GetFileName));
    }

    [Fact]
    public void ACopyOverItsBudgetIsUndoneAndReportedAsTooLarge()
    {
        string source = WriteFile("big/a.bin", new string('x', 4000));
        WriteFile("big/b.bin", new string('y', 4000));
        string destination = Directory.CreateDirectory(InRoot("dest")).FullName;

        var tight = new FileManager(_roots, _shell, NullLogger<FileManager>.Instance, maxCopyBytes: () => 1000);

        FileResult<FileEntry> folder = tight.Copy(InRoot("big"), destination, CancellationToken.None);
        FileResult<FileEntry> file = tight.Copy(source, destination, CancellationToken.None);

        Assert.Equal(FileStatus.TooLarge, folder.Status);
        Assert.Equal(FileStatus.TooLarge, file.Status);

        // Nothing half-written is left behind, and the source is untouched.
        Assert.Empty(Directory.GetFileSystemEntries(destination));
        Assert.Equal(4000, new FileInfo(source).Length);
    }

    [Fact]
    public void ACancelledCopyLeavesNothingBehind()
    {
        string source = WriteFile("slow.bin", new string('z', 5_000_000));
        string destination = Directory.CreateDirectory(InRoot("dest")).FullName;

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Equal(FileStatus.TooLarge, _files.Copy(source, destination, cancelled.Token).Status);
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    [Fact]
    public void CopyOfSomethingMissingOrIntoSomethingMissingIsNotFound()
    {
        string source = WriteFile("here.txt");

        Assert.Equal(FileStatus.NotFound, _files.Copy(InRoot("not-here.txt"), _root, CancellationToken.None).Status);
        Assert.Equal(FileStatus.NotFound, _files.Copy(source, InRoot("no-such-folder"), CancellationToken.None).Status);
        Assert.False(Directory.Exists(InRoot("no-such-folder")));
    }

    // ---------------------------------------------------------------------
    // file.move
    // ---------------------------------------------------------------------

    [Fact]
    public void MoveRelocatesAFileOrFolderIntoTheDestination()
    {
        string file = WriteFile("inbox/letter.txt", "dear");
        WriteFile("inbox/attachments/scan.png", "img");
        string destination = Directory.CreateDirectory(InRoot("filed")).FullName;

        FileResult<FileEntry> movedFile = _files.Move(file, destination, CancellationToken.None);
        FileResult<FileEntry> movedFolder = _files.Move(InRoot("inbox", "attachments"), destination, CancellationToken.None);

        Assert.True(movedFile.Succeeded, movedFile.Detail);
        Assert.Equal(Canonical(Path.Combine(destination, "letter.txt")), movedFile.Value!.Path);
        Assert.False(File.Exists(file));
        Assert.Equal("dear", File.ReadAllText(Path.Combine(destination, "letter.txt")));

        Assert.True(movedFolder.Succeeded, movedFolder.Detail);
        Assert.False(Directory.Exists(InRoot("inbox", "attachments")));
        Assert.Equal("img", File.ReadAllText(Path.Combine(destination, "attachments", "scan.png")));
    }

    [Fact]
    public void MoveRefusesWhenTheNameAlreadyExistsThere()
    {
        string source = WriteFile("from/same.txt", "incoming");
        string existing = WriteFile("to/same.txt", "resident");
        WriteFile("from/folder/x.txt");
        Directory.CreateDirectory(InRoot("to", "folder"));

        FileResult<FileEntry> file = _files.Move(source, InRoot("to"), CancellationToken.None);
        FileResult<FileEntry> folder = _files.Move(InRoot("from", "folder"), InRoot("to"), CancellationToken.None);

        // The contract's code for this collision.
        Assert.Equal(FileStatus.InvalidArguments, file.Status);
        Assert.Equal(FileStatus.InvalidArguments, folder.Status);

        Assert.Equal("incoming", File.ReadAllText(source));
        Assert.Equal("resident", File.ReadAllText(existing));
        Assert.True(File.Exists(InRoot("from", "folder", "x.txt")));
    }

    [Fact]
    public void MovingSomethingToWhereItAlreadyIsIsACollisionNotADeletion()
    {
        string file = WriteFile("stay.txt", "here");

        Assert.Equal(FileStatus.InvalidArguments, _files.Move(file, _root, CancellationToken.None).Status);
        Assert.Equal("here", File.ReadAllText(file));
    }

    // ---------------------------------------------------------------------
    // file.delete
    // ---------------------------------------------------------------------

    [Fact]
    public void DeleteGoesToTheRecycleBinByDefault()
    {
        string file = WriteFile("old.txt");
        Directory.CreateDirectory(InRoot("old-folder"));

        Assert.True(_files.Delete(file, permanent: false).Succeeded);
        Assert.True(_files.Delete(InRoot("old-folder"), permanent: false).Succeeded);

        // Handed to the shell, as canonical paths, and to nothing else.
        Assert.Equal([Canonical(file), Canonical(InRoot("old-folder"))], _shell.Recycled);
    }

    [Fact]
    public void ARecycleThatFailsIsNeverTurnedIntoAPermanentDelete()
    {
        string file = WriteFile("important.txt", "do not lose");

        _shell.Outcome = RecycleOutcome.Refused;
        FileResult<bool> refused = _files.Delete(file, permanent: false);

        _shell.Outcome = RecycleOutcome.AwaitingConfirmation;
        FileResult<bool> waiting = _files.Delete(file, permanent: false);

        Assert.Equal(FileStatus.AccessDenied, refused.Status);
        Assert.Equal(FileStatus.TooLarge, waiting.Status);
        Assert.False(string.IsNullOrWhiteSpace(refused.Detail));
        Assert.Equal("do not lose", File.ReadAllText(file));
    }

    [Fact]
    public void PermanentDeleteRemovesAFileOrAWholeFolderWithoutTheRecycleBin()
    {
        string file = WriteFile("temp.txt");
        WriteFile("tree/a/b/c.txt");

        Assert.True(_files.Delete(file, permanent: true).Succeeded);
        Assert.True(_files.Delete(InRoot("tree"), permanent: true).Succeeded);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(InRoot("tree")));
        Assert.Empty(_shell.Recycled);
    }

    [Fact]
    public void DeletingSomethingMissingIsNotFound()
    {
        Assert.Equal(FileStatus.NotFound, _files.Delete(InRoot("ghost.txt"), permanent: false).Status);
        Assert.Equal(FileStatus.NotFound, _files.Delete(InRoot("ghost.txt"), permanent: true).Status);
        Assert.Empty(_shell.Recycled);
    }

    [Fact]
    public void AnOperatingSystemRefusalIsAccessDeniedWithAReadableMessage()
    {
        string file = WriteFile("in-use.txt", "busy");

        // Held open with no sharing, the way a running program holds its own files.
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            FileResult<bool> deleted = _files.Delete(file, permanent: true);
            FileResult<FileEntry> renamed = _files.Rename(file, "renamed.txt");

            Assert.Equal(FileStatus.AccessDenied, deleted.Status);
            Assert.Equal(FileStatus.AccessDenied, renamed.Status);
            Assert.Contains("in use", deleted.Detail, StringComparison.OrdinalIgnoreCase);

            // The message is ours: it does not echo the PC's directory layout back.
            Assert.DoesNotContain(_base, deleted.Detail!, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(File.Exists(file));
    }

    // ---------------------------------------------------------------------
    // file.open
    // ---------------------------------------------------------------------

    [Fact]
    public void OpenHandsADocumentOrAFolderToTheShell()
    {
        string document = WriteFile("slides.pptx");

        Assert.True(_files.Open(document, mayLaunchPrograms: false).Succeeded);
        Assert.True(_files.Open(_root, mayLaunchPrograms: false).Succeeded);

        Assert.Equal([Canonical(document), Canonical(_root)], _shell.Opened);
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("run.bat")]
    [InlineData("script.ps1")]
    [InlineData("installer.msi")]
    [InlineData("Shortcut.lnk")]
    [InlineData("macro.VBS")]
    [InlineData("tweak.reg")]
    [InlineData("page.hta")]
    public void OpeningSomethingThatRunsNeedsTheLaunchPermissionAsWell(string name)
    {
        string program = WriteFile(name);

        FileResult<bool> withoutLaunch = _files.Open(program, mayLaunchPrograms: false);

        Assert.Equal(FileStatus.PermissionRequired, withoutLaunch.Status);
        Assert.Empty(_shell.Opened);

        Assert.True(_files.Open(program, mayLaunchPrograms: true).Succeeded);
        Assert.Equal([Canonical(program)], _shell.Opened);
    }

    [Fact]
    public void OpenOfSomethingMissingIsNotFoundAndAShellFailureIsReported()
    {
        Assert.Equal(FileStatus.NotFound, _files.Open(InRoot("ghost.docx"), mayLaunchPrograms: true).Status);

        string unknown = WriteFile("data.zzz");
        _shell.OpenSucceeds = false;

        FileResult<bool> result = _files.Open(unknown, mayLaunchPrograms: true);

        Assert.Equal(FileStatus.AccessDenied, result.Status);
        Assert.Equal("No app is set to open that type of file.", result.Detail);
    }

    // ---------------------------------------------------------------------
    // Fakes and helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Creates a directory link without elevation where possible. Returns false if this machine
    /// allows neither kind, in which case the calling test has nothing to check.
    /// </summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symbolic links need a privilege most accounts lack. Junctions do not.
        }

        try
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(link);
            start.ArgumentList.Add(target);

            using Process? process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class FixedRoots(params FileRoot[] roots) : IFileRootProvider
    {
        public List<FileRoot> Roots { get; } = [.. roots];

        public IReadOnlyList<FileRoot> GetRoots() => Roots;
    }

    /// <summary>
    /// Stands in for the Windows shell. Records what it was asked to recycle or open; a "recycle"
    /// removes the item with ordinary file APIs so later assertions see it gone.
    /// </summary>
    private sealed class FakeShell : IFileShell
    {
        public List<string> Recycled { get; } = [];

        public List<string> Opened { get; } = [];

        public RecycleOutcome Outcome { get; set; } = RecycleOutcome.Recycled;

        public bool OpenSucceeds { get; set; } = true;

        public RecycleOutcome MoveToRecycleBin(string path, out string? detail)
        {
            if (Outcome != RecycleOutcome.Recycled)
            {
                detail = Outcome == RecycleOutcome.Refused ? "The Recycle Bin refused that item." : null;
                return Outcome;
            }

            Recycled.Add(path);

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }

            detail = null;
            return RecycleOutcome.Recycled;
        }

        public bool Open(string path, out string? detail)
        {
            if (!OpenSucceeds)
            {
                detail = "No app is set to open that type of file.";
                return false;
            }

            Opened.Add(path);
            detail = null;
            return true;
        }
    }
}
