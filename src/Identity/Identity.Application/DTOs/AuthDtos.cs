using System.ComponentModel.DataAnnotations;

namespace Haworks.Identity.Application.DTOs;

public class UserRegistrationDto
{
    [Required(ErrorMessage = "Username is required.")]
    [MinLength(3, ErrorMessage = "Username must be at least 3 characters long.")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Invalid email address format.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
    public string Password { get; set; } = string.Empty;
}

public class UserLoginDto
{
    [Required(ErrorMessage = "Username is required.")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    public string Password { get; set; } = string.Empty;
}

public class RefreshTokenRequestDto
{
    [Required(ErrorMessage = "Access token is required.")]
    public string AccessToken { get; set; } = string.Empty;

    [Required(ErrorMessage = "Refresh token is required.")]
    public string RefreshToken { get; set; } = string.Empty;
}

public class AuthResponseDto
{
    public string Token { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Email { get; set; }
    public DateTime Expires { get; set; }
    public string? Message { get; set; }
}

public class TokenVerificationDto
{
    public string UserId { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public bool IsAuthenticated { get; set; }
    public string? Message { get; set; }
}
