using System.Net;
using System.Net.Http.Json;
using LiveAuction.Application.Auth;
using LiveAuction.Domain;
using LiveAuction.IntegrationTests.Infrastructure;

namespace LiveAuction.IntegrationTests;

public class AuthAndSecurityTests(LiveAuctionFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Register_returns_token_and_public_registration_is_always_bidder()
    {
        var client = Factory.CreateClient();

        // Aunque el cliente intente mandar "role": "Admin", el DTO no tiene ese campo.
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "Ana@Test.local ", displayName = "Ana", password = "Secreta123", role = "Admin" }, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        Assert.Equal(UserRole.Bidder, auth.User.Role);
        Assert.Equal("ana@test.local", auth.User.Email); // normalizado
        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
    }

    [Fact]
    public async Task Register_rejects_duplicate_email_and_weak_password()
    {
        await CreateBidderAsync("Ana");
        var client = Factory.CreateClient();

        var duplicate = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "ANA@test.local", displayName = "Otra", password = "Secreta123" }, Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("EmailAlreadyRegistered", await ProblemCodeAsync(duplicate));

        var weak = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "debil@test.local", displayName = "Debil", password = "solotexto" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_or_unknown_email_gives_same_401()
    {
        await CreateBidderAsync("Ana");
        var client = Factory.CreateClient();

        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { email = "ana@test.local", password = "Incorrecta1" }, Json);
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { email = "nadie@test.local", password = "Secreta123" }, Json);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await ProblemCodeAsync(wrong), await ProblemCodeAsync(unknown)); // no revela si el correo existe
    }

    [Fact]
    public async Task Me_requires_token_and_returns_own_user()
    {
        var ana = await CreateBidderAsync("Ana");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().GetAsync("/api/auth/me")).StatusCode);

        var me = await ana.Client.GetFromJsonAsync<UserDto>("/api/auth/me", Json);
        Assert.Equal(ana.Id, me!.Id);
    }

    [Fact]
    public async Task Tampered_or_foreign_token_is_rejected()
    {
        var client = Factory.CreateClient();
        // Token firmado con otra clave (alg HS256, sub=1, role=Admin).
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIiwicm9sZSI6IkFkbWluIiwiaXNzIjoiTGl2ZUF1Y3Rpb24iLCJhdWQiOiJMaXZlQXVjdGlvbiIsImV4cCI6NDEwMjQ0NDgwMH0.Zt5W2Nn3m0lJ0m7m9ki8b2m2yCZb9i1C0nB5XzVqk9k");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Only_admins_manage_auctions_and_anyone_can_browse()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin);

        var forbidden = await ana.Client.PostAsJsonAsync("/api/auctions", new
        {
            title = "Intento", startingPrice = 1, minIncrement = 1, startsAt = Now, endsAt = Now.AddHours(1)
        }, Json);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ana.Client.PostAsync($"/api/auctions/{auction.Id}/cancel", null)).StatusCode);

        var anonymous = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/auctions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/auctions/{auction.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/auctions/{auction.Id}/bids")).StatusCode);
    }

    [Fact]
    public async Task Bidding_requires_authentication()
    {
        var admin = await CreateAdminAsync();
        var auction = await CreateAuctionAsync(admin);

        var response = await Factory.CreateClient().PostAsJsonAsync($"/api/auctions/{auction.Id}/bids", new { amount = 100 }, Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Errors_are_problem_details_and_security_headers_are_present()
    {
        var response = await Factory.CreateClient().GetAsync("/api/auctions/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("AuctionNotFound", await ProblemCodeAsync(response));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Health_endpoint_reports_database()
    {
        var response = await Factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
