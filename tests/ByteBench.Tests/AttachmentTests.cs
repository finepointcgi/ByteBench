using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

public class AttachmentTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 1, 2, 3];

    private static MultipartFormDataContent File(string name, byte[] bytes, string contentType = "application/octet-stream")
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", name } };
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private string StoredPath(int attachmentId)
    {
        using var db = app.Db();
        return Path.Combine(app.AttachmentRoot, db.Attachments.Where(a => a.Id == attachmentId).Select(a => a.StoredName).First());
    }

    private async Task<(Project Project, Issue Issue)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        return (project, await app.CreateIssueAsync(project.Id, "carol", "Has files"));
    }

    [Fact]
    public async Task Upload_and_download_through_the_api_with_a_token()
    {
        var (project, issue) = await SetupAsync();
        var bob = await app.ApiClientAsync("bob");
        var url = $"/api/v1/projects/{project.Key}/issues/{issue.Number}/attachments";

        var created = await bob.PostAsync(url, File("../../screenshot.png", Png, "image/png"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await Json(created);
        Assert.Equal("screenshot.png", dto.GetProperty("fileName").GetString()); // directories stripped
        Assert.Equal("image/png", dto.GetProperty("contentType").GetString());
        Assert.Equal(Png.Length, dto.GetProperty("size").GetInt64());
        Assert.StartsWith("![screenshot.png](/attachments/", dto.GetProperty("markdown").GetString());
        var id = dto.GetProperty("id").GetInt32();
        Assert.True(System.IO.File.Exists(StoredPath(id)));

        var list = await Json(await bob.GetAsync(url));
        Assert.Equal(id, Assert.Single(list.EnumerateArray()).GetProperty("id").GetInt32());

        var download = await bob.GetAsync($"{url}/{id}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("sandbox", download.Headers.GetValues("Content-Security-Policy").Single());

        // Delete through the API removes the file.
        var path = StoredPath(id);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.DeleteAsync($"{url}/{id}")).StatusCode);
        Assert.False(System.IO.File.Exists(path));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"{url}/{id}")).StatusCode);
    }

    [Fact]
    public async Task Only_real_raster_images_are_served_inline()
    {
        var (project, issue) = await SetupAsync();
        var bob = await app.ApiClientAsync("bob");
        var url = $"/api/v1/projects/{project.Key}/issues/{issue.Number}/attachments";

        async Task<HttpResponseMessage> RoundTrip(string name, byte[] bytes, string declared)
        {
            var dto = await Json(await bob.PostAsync(url, File(name, bytes, declared)));
            return await bob.GetAsync($"{url}/{dto.GetProperty("id").GetInt32()}");
        }

        var svg = await RoundTrip("logo.svg", "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray(), "image/svg+xml");
        var fakePng = await RoundTrip("fake.png", "<html><script>alert(1)</script></html>"u8.ToArray(), "image/png");
        var html = await RoundTrip("page.html", "<html></html>"u8.ToArray(), "text/html");
        foreach (var r in new[] { svg, fakePng, html })
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("application/octet-stream", r.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", r.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        }
        Assert.Equal("page.html", html.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        // A real GIF under a misleading name is still recognised by its bytes.
        var gif = await RoundTrip("animation.bin", "GIF89a\x01\x00\x01\x00"u8.ToArray(), "application/octet-stream");
        Assert.Equal("image/gif", gif.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", gif.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task Permissions_on_upload_and_download()
    {
        var (project, issue) = await SetupAsync();
        var url = $"/api/v1/projects/{project.Key}/issues/{issue.Number}/attachments";
        var carol = await app.ApiClientAsync("carol");
        var id = (await Json(await carol.PostAsync(url, File("notes.txt", "secret notes"u8.ToArray())))).GetProperty("id").GetInt32();

        // Viewer: can download, can't upload. Read-only token: can't upload.
        var dan = await app.ApiClientAsync("dan");
        Assert.Equal(HttpStatusCode.OK, (await dan.GetAsync($"{url}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dan.PostAsync(url, File("x.txt", [1]))).StatusCode);
        var readOnly = await app.ApiClientAsync("bob", TokenScopes.ReadApi);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync(url, File("x.txt", [1]))).StatusCode);

        // Someone who can't see the project gets 404 from both routes, like a missing attachment.
        await app.CreateUserAsync("attachoutsider");
        var outsider = await app.ApiClientAsync("attachoutsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{url}/{id}")).StatusCode);
        var outsiderBrowser = await app.CookieClientAsync("attachoutsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderBrowser.GetAsync($"/attachments/{id}/notes.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderBrowser.GetAsync($"/attachments/999999/notes.txt")).StatusCode);

        // The browser route needs a session; with one, a member gets the file.
        var anonymous = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anon = await anonymous.GetAsync($"/attachments/{id}/notes.txt");
        Assert.True(anon.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect, $"got {(int)anon.StatusCode}");
        var bobBrowser = await app.CookieClientAsync("bob");
        var ok = await bobBrowser.GetAsync($"/attachments/{id}/notes.txt");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("secret notes", await ok.Content.ReadAsStringAsync());

        // Only the uploader or a Maintainer+ may delete.
        var bob = await app.ApiClientAsync("bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"{url}/{id}")).StatusCode);
    }

    [Fact]
    public async Task Files_are_deleted_with_their_issue_and_sub_tasks()
    {
        var (project, story) = await SetupAsync();
        var sub = await app.CreateIssueAsync(project.Id, "carol", "sub", IssueType.Subtask, parentId: story.Id);
        var other = await app.CreateIssueAsync(project.Id, "carol", "bulk me");
        var carol = app.UserId("carol");
        int a1, a2, a3;
        await using (var s = app.Scope(carol))
        {
            var attachments = s.Get<ByteBench.Services.Attachments.AttachmentService>();
            a1 = (await attachments.UploadAsync(story.Id, "a.txt", new MemoryStream("a"u8.ToArray()))).Id;
            a2 = (await attachments.UploadAsync(sub.Id, "b.txt", new MemoryStream("b"u8.ToArray()))).Id;
            a3 = (await attachments.UploadAsync(other.Id, "c.txt", new MemoryStream("c"u8.ToArray()))).Id;
        }
        var paths = new[] { StoredPath(a1), StoredPath(a2), StoredPath(a3) };
        Assert.All(paths, p => Assert.True(System.IO.File.Exists(p)));

        await using (var s = app.Scope(carol))
        {
            await s.Get<IssueService>().DeleteIssueAsync(story.Id);
            Assert.False(System.IO.File.Exists(paths[0]));
            Assert.False(System.IO.File.Exists(paths[1])); // the sub-task's file went with it
            Assert.True(System.IO.File.Exists(paths[2]));

            await s.Get<IssueService>().BulkUpdateAsync([other.Id], new BulkIssueChange { Delete = true }, carol);
            Assert.False(System.IO.File.Exists(paths[2]));
        }
        await using var db = app.Db();
        Assert.False(await db.Attachments.AnyAsync(a => a.Id == a1 || a.Id == a2 || a.Id == a3));
    }
}
