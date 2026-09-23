using System.Security.Claims;
using System.Text;
using ClinicApp.Application.DTOs;
using ClinicApp.Application.Interfaces;
using ClinicApp.Domain.Entities;
using ClinicApp.Infrastructure.Data;
using ClinicApp.Infrastructure.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace ClinicApp.Infrastructure.Services.Implementations
{
    public class UserService : IUserService
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IEmailService _emailService;

        public UserService(AppDbContext context, IConfiguration configuration, IEmailService emailService)
        {
            _context = context;
            _configuration = configuration;
            _emailService = emailService;
        }

        public object Login(LoginDto dto)
        {
            var username = dto.Username.Trim().ToLowerInvariant();

            var user = _context.Users.FirstOrDefault(x =>
                x.Username.ToLower() == username);

            if (user == null || !PasswordHelper.VerifyPassword(dto.Password, user.PasswordHash))
                throw new UnauthorizedAccessException("Pogresan username ili lozinka.");

            var token = GenerateJwtToken(user);
            var refreshToken = GenerateRefreshToken();

            user.RefreshToken = refreshToken;
            user.RefreshTokenCreatedAt = DateTime.UtcNow;
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(GetRefreshTokenExpiryDays());

            _context.SaveChanges();

            return new
            {
                token,
                refreshToken,
                user.Id,
                user.Username,
                user.Role,
                user.MustChangePassword
            };
        }

        public object Refresh(RefreshTokenRequestDto dto)
        {
            var user = _context.Users.FirstOrDefault(x =>
                x.Id == dto.UserId &&
                x.RefreshToken == dto.RefreshToken);

            if (user == null || user.RefreshTokenExpiryTime < DateTime.UtcNow)
                throw new UnauthorizedAccessException("Refresh token nije ispravan.");

            var newToken = GenerateJwtToken(user);
            var newRefresh = GenerateRefreshToken();

            user.RefreshToken = newRefresh;
            user.RefreshTokenCreatedAt = DateTime.UtcNow;
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(GetRefreshTokenExpiryDays());

            _context.SaveChanges();

            return new
            {
                token = newToken,
                refreshToken = newRefresh,
                user.Id,
                user.Username,
                user.Role,
                user.MustChangePassword
            };
        }

        public void Logout(int userId)
        {
            var user = _context.Users.Find(userId);
            if (user == null) return;

            user.RefreshToken = null;
            user.RefreshTokenExpiryTime = null;

            _context.SaveChanges();
        }

        public object GetMe(int userId)
        {
            var user = _context.Users.Find(userId);
            if (user == null) throw new Exception("Korisnik nije pronadjen.");

            return new
            {
                user.Id,
                user.Username,
                user.Role,
                user.MustChangePassword
            };
        }
        public async Task<PagedResultDto<UserListItemDto>> GetPagedUsersAsync(
            ListUsersRequestDto request,
            CancellationToken ct = default)
        {
            var page = request.Page < 1 ? 1 : request.Page;
            var pageSize = request.PageSize is < 1 or > 100 ? 10 : request.PageSize;

            var query = _context.Users
                .AsNoTracking()
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                var search = request.Search.Trim();

                query = query.Where(x =>
                    x.Username.Contains(search) ||
                    (x.Email != null && x.Email.Contains(search)));
            }

            if (!string.IsNullOrWhiteSpace(request.Role))
            {
                var role = request.Role.Trim().ToLower();

                query = query.Where(x => x.Role.ToLower() == role);
            }

            var totalCount = await query.CountAsync(ct);

            var items = await query
                .OrderBy(x => x.Username)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new UserListItemDto
                {
                    Id = x.Id,
                    Username = x.Username,
                    Email = x.Email,
                    Role = x.Role,
                    MustChangePassword = x.MustChangePassword,
                    LastSuccessfulLoginAtUtc = x.LastSuccessfulLoginAtUtc
                })
                .ToListAsync(ct);

            return new PagedResultDto<UserListItemDto>
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount
            };
        }

        public async Task<object> AddUserAsync(AddUserDto dto, string adminUsername)
        {
            var frontendBaseUrl = RequireSetting("App:FrontendBaseUrl").TrimEnd('/');
            User user = null!;

            // SQL Server je podesen sa EnableRetryOnFailure, pa rucna transakcija
            // mora ici kroz execution strategy (inace EF baca gresku).
            var strategy = _context.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();

                var tempPassword = PasswordHelper.GenerateTemporaryPassword();
                var token = Guid.NewGuid().ToString();

                user = new User
                {
                    Username = dto.Username,
                    Email = dto.Email,
                    Role = dto.Role,
                    PasswordHash = PasswordHelper.HashPassword(tempPassword),
                    MustChangePassword = true,
                    PasswordResetTokenHash = PasswordHelper.HashPassword(token),
                    PasswordResetTokenExpiryTime = DateTime.UtcNow.AddMinutes(30),
                    PasswordResetRequestedAt = DateTime.UtcNow
                };

                await using var transaction = await _context.Database.BeginTransactionAsync();

                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                var link = $"{frontendBaseUrl}/reset-password?token={Uri.EscapeDataString(token)}";

                // Ako slanje maila ne uspije, transakcija se ne commita i korisnik se ne kreira.
                await _emailService.SendPasswordSetupEmailAsync(user.Email!, user.Username, link);
                await transaction.CommitAsync();
            });

            return new
            {
                user.Id,
                user.Username,
                user.Email,
                user.Role,
                setupLinkSent = true
            };
        }

        public object ChangePassword(int userId, ChangePasswordDto dto)
        {
            var user = _context.Users.Find(userId);
            if (user == null) throw new Exception("Korisnik nije pronadjen.");

            if (!PasswordHelper.VerifyPassword(dto.CurrentPassword, user.PasswordHash))
                throw new UnauthorizedAccessException("Trenutna lozinka nije ispravna.");

            user.PasswordHash = PasswordHelper.HashPassword(dto.NewPassword);
            user.MustChangePassword = false;

            _context.SaveChanges();

            return new { message = "Lozinka je uspjesno promijenjena." };
        }

        public async Task<object> RequestPasswordResetAsync(RequestPasswordResetDto dto)
        {
            var lookup = dto.UsernameOrEmail.Trim().ToLowerInvariant();

            var user = _context.Users.FirstOrDefault(x =>
                x.Username.ToLower() == lookup ||
                (x.Email != null && x.Email.ToLower() == lookup));

            if (user == null)
                return new { message = "Ako nalog postoji, reset link je poslan na email." };

            if (string.IsNullOrWhiteSpace(user.Email))
                return new { message = "Ako nalog postoji, reset link je poslan na email." };

            var token = Guid.NewGuid().ToString();

            user.PasswordResetTokenHash = PasswordHelper.HashPassword(token);
            user.PasswordResetTokenExpiryTime = DateTime.UtcNow.AddMinutes(30);

            _context.SaveChanges();

            var frontendBaseUrl = RequireSetting("App:FrontendBaseUrl").TrimEnd('/');
            var link = $"{frontendBaseUrl}/reset-password?token={Uri.EscapeDataString(token)}";

            await _emailService.SendPasswordResetEmailAsync(user.Email!, user.Username, link);

            return new { message = "Ako nalog postoji, reset link je poslan na email." };
        }

        public object ConfirmPasswordReset(ConfirmPasswordResetDto dto)
        {
            var user = _context.Users.ToList().FirstOrDefault(u =>
                u.PasswordResetTokenHash != null &&
                PasswordHelper.VerifyPassword(dto.Token, u.PasswordResetTokenHash));

            if (user == null || user.PasswordResetTokenExpiryTime < DateTime.UtcNow)
                throw new InvalidOperationException("Reset link nije ispravan ili je istekao.");

            user.PasswordHash = PasswordHelper.HashPassword(dto.NewPassword);
            user.PasswordResetTokenHash = null;
            user.PasswordResetTokenExpiryTime = null;
            user.MustChangePassword = false;

            _context.SaveChanges();

            return new { message = "Lozinka je uspjesno resetovana." };
        }

        public object ResetUserPassword(int userId, string adminUsername)
        {
            var user = _context.Users.Find(userId);
            if (user == null) throw new Exception("Korisnik nije pronadjen.");

            var tempPassword = PasswordHelper.GenerateTemporaryPassword();

            user.PasswordHash = PasswordHelper.HashPassword(tempPassword);
            user.MustChangePassword = true;

            _context.SaveChanges();

            return new
            {
                user.Username,
                temporaryPassword = tempPassword
            };
        }

        public object DeleteUser(int userId, int currentUserId)
        {
            if (userId == currentUserId)
                throw new InvalidOperationException("Ne možete obrisati vlastiti nalog.");

            var user = _context.Users.Find(userId);
            if (user == null) throw new Exception("Korisnik nije pronadjen.");

            if (user.Role.ToLower() == "admin")
            {
                var adminCount = _context.Users.Count(x => x.Role.ToLower() == "admin");
                if (adminCount <= 1)
                    throw new InvalidOperationException("Ne možete obrisati zadnjeg admin korisnika.");
            }

            _context.Users.Remove(user);
            _context.SaveChanges();

            return new { message = "Korisnik je obrisan." };
        }

        public object GetLogs()
        {
            return _context.Logs
                .OrderByDescending(x => x.Timestamp)
                .Take(100)
                .ToList();
        }

        // -----------------------
        // PRIVATE METHODS
        // -----------------------

        private string GenerateJwtToken(User user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(RequireSetting("Jwt:Key")));

            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role)
            };

            var token = new JwtSecurityToken(
                RequireSetting("Jwt:Issuer"),
                RequireSetting("Jwt:Audience"),
                claims,
                expires: DateTime.UtcNow.AddMinutes(RequireIntegerSetting("Jwt:ExpiryMinutes")),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private string GenerateRefreshToken()
        {
            return Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        }

        private int GetRefreshTokenExpiryDays()
        {
            return RequireIntegerSetting("Jwt:RefreshTokenExpiryDays");
        }

        private string RequireSetting(string key)
        {
            return _configuration[key]
                ?? throw new InvalidOperationException($"Missing required configuration value: {key}");
        }

        private int RequireIntegerSetting(string key)
        {
            var rawValue = RequireSetting(key);

            return int.TryParse(rawValue, out var value)
                ? value
                : throw new InvalidOperationException($"Configuration value '{key}' must be a valid integer.");
        }
    }
}
