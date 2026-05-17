using FluentAssertions;
using Haworks.Identity.Domain;
using Xunit;

namespace Haworks.Identity.Unit.Domain;

public class UserTests
{
    #region Constructor and Default Values Tests

    [Fact]
    public void Constructor_Default_CreatesUserWithDefaults()
    {
        // Act
        var user = new User();

        // Assert
        user.IsActive.Should().BeTrue();
        user.CheckoutSessionId.Should().BeNull();
        user.StripeCustomerId.Should().BeNull();
        user.Profile.Should().BeNull();
        user.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public void CreatedAt_DefaultIsSet()
    {
        // Arrange
        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        var user = new User();

        // Assert
        var after = DateTime.UtcNow.AddSeconds(1);
        user.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    #endregion

    #region IdentityUser Properties Tests

    [Fact]
    public void Id_CanBeSet()
    {
        // Arrange
        var user = new User();
        var id = Guid.NewGuid().ToString();

        // Act
        user.Id = id;

        // Assert
        user.Id.Should().Be(id);
    }

    [Fact]
    public void UserName_CanBeSet()
    {
        // Arrange
        var user = new User();
        var username = "testuser";

        // Act
        user.UserName = username;

        // Assert
        user.UserName.Should().Be(username);
    }

    [Fact]
    public void Email_CanBeSet()
    {
        // Arrange
        var user = new User();
        var email = "test@example.com";

        // Act
        user.Email = email;

        // Assert
        user.Email.Should().Be(email);
    }

    #endregion

    #region Custom Properties Tests

    [Fact]
    public void CheckoutSessionId_CanBeSetAndCleared()
    {
        // Arrange
        var user = new User();
        var sessionId = "cs_test_123";

        // Act - Set
        user.CheckoutSessionId = sessionId;
        user.CheckoutSessionId.Should().Be(sessionId);

        // Act - Clear
        user.CheckoutSessionId = null;
        user.CheckoutSessionId.Should().BeNull();
    }

    [Fact]
    public void StripeCustomerId_CanBeSetAndCleared()
    {
        // Arrange
        var user = new User();
        var customerId = "cus_test_789";

        // Act - Set
        user.StripeCustomerId = customerId;
        user.StripeCustomerId.Should().Be(customerId);

        // Act - Clear
        user.StripeCustomerId = null;
        user.StripeCustomerId.Should().BeNull();
    }

    #endregion

    #region Profile Relationship Tests

    [Fact]
    public void Profile_CanBeAssociated()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid().ToString() };
        var profile = UserProfile.Create(user.Id);

        // Act
        user.Profile = profile;

        // Assert
        user.Profile.Should().NotBeNull();
        user.Profile!.UserId.Should().Be(user.Id);
    }

    #endregion

    #region IsActive Tests

    [Fact]
    public void Deactivate_WhenActive_SetsIsActiveFalseAndDeactivatedAt()
    {
        // Arrange
        var user = new User();
        user.IsActive.Should().BeTrue();

        // Act
        user.Deactivate();

        // Assert
        user.IsActive.Should().BeFalse();
        user.DeactivatedAt.Should().NotBeNull();
        user.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public void Deactivate_WhenAlreadyDeactivated_Throws()
    {
        // Arrange
        var user = new User();
        user.Deactivate();

        // Act & Assert
        var act = () => user.Deactivate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already deactivated*");
    }

    [Fact]
    public void Reactivate_WhenDeactivated_SetsIsActiveTrue()
    {
        // Arrange
        var user = new User();
        user.Deactivate();

        // Act
        user.Reactivate();

        // Assert
        user.IsActive.Should().BeTrue();
        user.DeactivatedAt.Should().BeNull();
    }

    [Fact]
    public void Reactivate_WhenAlreadyActive_Throws()
    {
        // Arrange
        var user = new User();

        // Act & Assert
        var act = () => user.Reactivate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already active*");
    }

    [Fact]
    public void DeactivateAndReactivate_Workflow()
    {
        // Arrange
        var user = new User();
        user.IsActive.Should().BeTrue();

        // Act - Deactivate
        user.Deactivate();

        // Assert
        user.IsActive.Should().BeFalse();
        user.UpdatedAt.Should().NotBeNull();
        user.DeactivatedAt.Should().NotBeNull();

        // Act - Reactivate
        user.Reactivate();

        // Assert
        user.IsActive.Should().BeTrue();
        user.DeactivatedAt.Should().BeNull();
    }

    #endregion
}

public class UserProfileTests
{
    [Fact]
    public void Create_WithValidUserId_SetsInitialState()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();

        // Act
        var profile = UserProfile.Create(userId);

        // Assert
        profile.UserId.Should().Be(userId);
        profile.Country.Should().Be("US");
        profile.FirstName.Should().BeEmpty();
    }

    [Fact]
    public void UpdatePersonalInfo_UpdatesFieldsAndTimestamp()
    {
        // Arrange
        var profile = UserProfile.Create(Guid.NewGuid().ToString());
        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        profile.UpdatePersonalInfo("John", "Doe", "+1234567890");

        // Assert
        profile.FirstName.Should().Be("John");
        profile.LastName.Should().Be("Doe");
        profile.Phone.Should().Be("+1234567890");
        profile.UpdatedAt.Should().BeAfter(before);
    }

    [Fact]
    public void RecordLogin_SetsLastLogin()
    {
        // Arrange
        var profile = UserProfile.Create(Guid.NewGuid().ToString());

        // Act
        profile.RecordLogin();

        // Assert
        profile.LastLogin.Should().NotBeNull();
    }
}
