using Relayo.Core;
using Relayo.Core.Files;

namespace Relayo.Tests;

public class AttachmentPolicyTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private static readonly AttachmentLimits Limits =
        new(1024, ["image/png", "image/jpeg", "application/pdf", "text/plain"], MaxFiles: 2);

    [Fact]
    public void Accepts_file_whose_content_matches_the_type()
    {
        var file = Assert.Single(AttachmentPolicy.Check([new FileUpload("shot.png", "image/png", Png)], Limits));
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal("shot.png", file.FileName);
    }

    [Fact]
    public void Type_comes_from_extension_when_browser_does_not_name_it()
    {
        var file = AttachmentPolicy.Check([new FileUpload("doc.PDF", "application/octet-stream", "%PDF-1.4"u8.ToArray())], Limits)[0];
        Assert.Equal("application/pdf", file.ContentType);
    }

    [Theory]
    [InlineData("evil.png", "image/png")]      // HTML под видом картинки
    [InlineData("evil.html", "text/html")]     // тип не разрешён
    [InlineData("run.exe", null)]              // неизвестное расширение
    public void Rejects_disguised_or_unknown_files(string name, string? type)
    {
        var ex = Assert.Throws<DomainException>(() =>
            AttachmentPolicy.Check([new FileUpload(name, type, "<html><script>x</script></html>"u8.ToArray())], Limits));
        Assert.Equal(ErrorCodes.AttachmentTypeNotAllowed, ex.Code);
    }

    [Fact]
    public void Binary_is_not_accepted_as_text()
    {
        var ex = Assert.Throws<DomainException>(() =>
            AttachmentPolicy.Check([new FileUpload("a.txt", "text/plain", [0x4D, 0x5A, 0x00, 0x01])], Limits));
        Assert.Equal(ErrorCodes.AttachmentTypeNotAllowed, ex.Code);
    }

    [Fact]
    public void Total_size_and_count_are_limited()
    {
        var big = new FileUpload("a.txt", "text/plain", new byte[600]);
        Assert.Equal(ErrorCodes.AttachmentTooLarge,
            Assert.Throws<DomainException>(() => AttachmentPolicy.Check([big, big], Limits)).Code);
        var small = new FileUpload("a.txt", "text/plain", "x"u8.ToArray());
        Assert.Equal(ErrorCodes.TooManyAttachments,
            Assert.Throws<DomainException>(() => AttachmentPolicy.Check([small, small, small], Limits)).Code);
    }

    [Theory]
    [InlineData("C:\\Users\\me\\report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a\"b<c>.txt", "abc.txt")]
    [InlineData("...", "file")]
    [InlineData("", "file")]
    public void File_name_is_cleaned(string raw, string expected) =>
        Assert.Equal(expected, AttachmentPolicy.CleanFileName(raw));

    [Fact]
    public void Long_file_name_keeps_extension()
    {
        var name = AttachmentPolicy.CleanFileName(new string('a', 500) + ".pdf");
        Assert.Equal(AttachmentPolicy.FileNameMaxLength, name.Length);
        Assert.EndsWith(".pdf", name);
    }
}
