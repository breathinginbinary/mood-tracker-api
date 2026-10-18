using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MoodTracker.Api.Data;

namespace MoodTracker.Api.Auth;

public record RegisterRequest(string? Email, string? Password);
public record LoginRequest(string? Email, string? Password);
public record AuthResponse(string Token, DateTime ExpiresAt);
public record MeResponse(string Id, string? Email);

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/auth");

        auth.MapPost("/register", async (RegisterRequest req, UserManager<ApplicationUser> users, MoodDbContext db, TokenService tokens) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password))
                return Results.BadRequest("Email and password are required.");

            // The first account inherits everything recorded before login existed.
            await using var tx = await db.Database.BeginTransactionAsync();
            var isFirst = !await users.Users.AnyAsync();

            var email = req.Email.Trim();
            var user = new ApplicationUser { UserName = email, Email = email };
            var created = await users.CreateAsync(user, req.Password);
            if (!created.Succeeded)
                return Results.ValidationProblem(created.Errors
                    .GroupBy(e => e.Code)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));

            if (isFirst) await ClaimExistingDataAsync(db, user.Id);
            await tx.CommitAsync();

            var token = tokens.Create(user);
            return Results.Ok(new AuthResponse(token.Token, token.ExpiresAt));
        }).AllowAnonymous();

        auth.MapPost("/login", async (LoginRequest req, UserManager<ApplicationUser> users, TokenService tokens) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password))
                return Results.BadRequest("Email and password are required.");

            var user = await users.FindByEmailAsync(req.Email.Trim());
            if (user is null) return Results.Unauthorized();
            if (await users.IsLockedOutAsync(user))
                return Results.Problem("Too many failed attempts. Try again later.", statusCode: StatusCodes.Status429TooManyRequests);

            if (!await users.CheckPasswordAsync(user, req.Password))
            {
                await users.AccessFailedAsync(user);
                return Results.Unauthorized();
            }

            await users.ResetAccessFailedCountAsync(user);
            var token = tokens.Create(user);
            return Results.Ok(new AuthResponse(token.Token, token.ExpiresAt));
        }).AllowAnonymous();

        auth.MapGet("/me", async (ICurrentUser current, UserManager<ApplicationUser> users) =>
        {
            var user = current.Id is null ? null : await users.FindByIdAsync(current.Id);
            return user is null ? Results.Unauthorized() : Results.Ok(new MeResponse(user.Id, user.Email));
        });
    }

    // Rows created before login existed have no owner. Hand them to the first user, along with the
    // condition switches they had set, so nothing is lost. Later users start from the condition defaults.
    private static async Task ClaimExistingDataAsync(MoodDbContext db, string userId)
    {
        await db.Entries.IgnoreQueryFilters().Where(e => e.UserId == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.UserId, userId));
        await db.Factors.IgnoreQueryFilters().Where(f => f.UserId == null).ExecuteUpdateAsync(s => s.SetProperty(f => f.UserId, userId));
        await db.Medications.IgnoreQueryFilters().Where(m => m.UserId == null).ExecuteUpdateAsync(s => s.SetProperty(m => m.UserId, userId));

        var conditions = await db.Conditions.ToListAsync();
        db.UserConditions.AddRange(conditions.Select(c => new UserCondition { UserId = userId, ConditionId = c.Id, Enabled = c.Enabled }));
        await db.SaveChangesAsync();
    }
}
