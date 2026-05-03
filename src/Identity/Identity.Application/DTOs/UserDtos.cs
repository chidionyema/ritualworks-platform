using System.ComponentModel.DataAnnotations;

namespace Haworks.Identity.Application.DTOs;

public class UserProfileDto
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = "US";
    public string Bio { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
}

public class UpdateProfileDto
{
    [Required]
    [StringLength(50)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string LastName { get; set; } = string.Empty;

    [Phone]
    public string Phone { get; set; } = string.Empty;

    [StringLength(100)]
    public string Address { get; set; } = string.Empty;

    [StringLength(50)]
    public string City { get; set; } = string.Empty;

    [StringLength(50)]
    public string State { get; set; } = string.Empty;

    [StringLength(20)]
    public string PostalCode { get; set; } = string.Empty;

    [StringLength(2)]
    public string Country { get; set; } = "US";

    [StringLength(500)]
    public string Bio { get; set; } = string.Empty;

    [StringLength(100)]
    [Url]
    public string Website { get; set; } = string.Empty;
}

public class ShippingInfoDto
{
    [Required]
    [StringLength(50)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string Address { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string City { get; set; } = string.Empty;

    [StringLength(50)]
    public string State { get; set; } = string.Empty;

    [Required]
    [StringLength(20)]
    public string PostalCode { get; set; } = string.Empty;

    [Required]
    [StringLength(2)]
    public string Country { get; set; } = "US";

    [Phone]
    public string Phone { get; set; } = string.Empty;
}
