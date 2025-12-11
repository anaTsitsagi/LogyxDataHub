using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace LogyxDataHub.Controllers
{
    [ApiController]
    [Route("auth")]
    public class AuthController : ControllerBase
    {
        private readonly IConfiguration _config;

        public AuthController(IConfiguration config) => _config = config;

        // POST /auth/token
        // Body: { "username": "dev", "password": "dev" }
        // Dev-only token issuer. Returns { access_token, expires_in }.
        [HttpPost("token")]
        [AllowAnonymous]
        public IActionResult Token([FromBody] Credential cred)
        {
            if (cred is null || string.IsNullOrWhiteSpace(cred.Username) || string.IsNullOrWhiteSpace(cred.Password))
                return BadRequest("username and password required.");

            // Dev: simple validation. Replace with your real user check in production.
            if (cred.Username != "dev" || cred.Password != "dev")
                return Unauthorized();

            var key = _config["Jwt:Key"];
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(500, "JWT key not configured.");

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, cred.Username),
                new Claim("Identity", cred.Identity ?? "dev-identity"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var keyBytes = Encoding.UTF8.GetBytes(key);
            var creds = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);

            var expires = DateTime.UtcNow.AddHours(6);

            var token = new JwtSecurityToken(
                issuer: null,
                audience: null,
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: expires,
                signingCredentials: creds);

            var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

            return Ok(new { access_token = tokenString, expires_in = (int)(expires - DateTime.UtcNow).TotalSeconds });
        }

        public class Credential
        {
            public string? Username { get; set; }
            public string? Password { get; set; }
            public string? Identity { get; set; }
        }
    }
}