using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Haworks.Notifications.Infrastructure.Channels.Push.Fcm;
using Xunit;

namespace Haworks.Notifications.Unit.Channels.Push.Fcm;

[Trait("Category", "Unit")]
public sealed class FcmOptionsTests
{
    [Fact]
    public void FcmOptions_Validation_ShouldRequireProjectIdAndServiceAccountJson()
    {
        var opts = new FcmOptions(); // empty
        var context = new ValidationContext(opts);
        var results = new List<ValidationResult>();
        
        var isValid = Validator.TryValidateObject(opts, context, results, true);
        
        isValid.Should().BeFalse();
        results.Should().HaveCount(2);
        results.Should().Contain(r => r.MemberNames.Contains("ProjectId"));
        results.Should().Contain(r => r.MemberNames.Contains("ServiceAccountJson"));
    }

    [Fact]
    public void FcmOptions_Validation_HappyPath()
    {
        var opts = new FcmOptions 
        { 
            ProjectId = "test-project", 
            ServiceAccountJson = "{}" 
        };
        var context = new ValidationContext(opts);
        var results = new List<ValidationResult>();
        
        var isValid = Validator.TryValidateObject(opts, context, results, true);
        
        isValid.Should().BeTrue();
    }
}
