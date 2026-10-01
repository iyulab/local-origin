using LocalOrigin.Files;

namespace LocalOrigin.Tests.Files;

public sealed class ScopeFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("local-origin-folder-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("/index.html", "index.html")]
    [InlineData("data/notes.json", "data/notes.json")]
    [InlineData("/a b/한글.txt", "a b/한글.txt")]
    [InlineData("/.hidden/x", ".hidden/x")]
    [InlineData("/report%2Fdraft.txt", "report%2Fdraft.txt")]
    public void A_path_inside_the_folder_names_its_file(string path, string relative)
    {
        var resolved = new ScopeFolder(_root).Resolve(path);

        Assert.NotNull(resolved);
        Assert.Equal(relative, resolved.Relative);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, relative)), resolved.FullPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/data/")]
    [InlineData("//x")]
    [InlineData("/../x")]
    [InlineData("/a/../../x")]
    [InlineData("/./x")]
    [InlineData("/a/..")]
    [InlineData(@"/..\x")]
    [InlineData(@"/a\b")]
    [InlineData(@"/\\server\share\x")]
    [InlineData("/C:/Windows/win.ini")]
    [InlineData("/C:x")]
    [InlineData("/notes.txt:secret")]
    [InlineData("/notes.txt::$DATA")]
    [InlineData("/notes.txt.")]
    [InlineData("/notes.txt ")]
    [InlineData("/data./x")]
    [InlineData("/CON")]
    [InlineData("/nul.txt")]
    [InlineData("/data/com1.json")]
    [InlineData("/LPT9 .log")]
    [InlineData("/a*b")]
    [InlineData("/a?b")]
    [InlineData("/a<b")]
    [InlineData("/a|b")]
    [InlineData("/a\"b")]
    [InlineData("/a\u0000b")]
    [InlineData("/a\u001fb")]
    public void A_path_that_could_reach_elsewhere_names_nothing(string? path)
    {
        Assert.Null(new ScopeFolder(_root).Resolve(path));
    }

    [Fact]
    public void A_sibling_folder_sharing_the_name_prefix_is_not_inside()
    {
        var sibling = _root + "-other";
        Directory.CreateDirectory(sibling);
        try
        {
            var folder = new ScopeFolder(_root);
            Assert.StartsWith(folder.Root + Path.DirectorySeparatorChar, folder.Resolve("/x")!.FullPath, StringComparison.Ordinal);
            Assert.Null(folder.Resolve("/../" + Path.GetFileName(sibling) + "/x"));
        }
        finally
        {
            Directory.Delete(sibling);
        }
    }

    [Fact]
    public void A_path_through_a_link_names_nothing()
    {
        var outside = Directory.CreateTempSubdirectory("local-origin-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
                File.CreateSymbolicLink(Path.Combine(_root, "file-link.txt"), Path.Combine(outside, "secret.txt"));
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                Assert.Skip("This system does not let the test create links.");
            }

            var folder = new ScopeFolder(_root);
            Assert.Null(folder.Resolve("/linked/secret.txt"));
            Assert.Null(folder.Resolve("/linked/new.txt"));
            Assert.Null(folder.Resolve("/file-link.txt"));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void The_folder_must_exist()
    {
        Assert.Throws<DirectoryNotFoundException>(() => new ScopeFolder(Path.Combine(_root, "missing")));
    }
}
