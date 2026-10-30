using SupportRag.Core.Generation;

namespace SupportRag.Tests;

public class LicensingTests
{
    [Theory]
    [InlineData("I lost my license email, what do I do?")]
    [InlineData("Where do the license files go on Windows?")]
    [InlineData("How many computers can I install it on?")]
    [InlineData("Does the bundle include plugins you release later?")]
    [InlineData("is there a free trial")]
    public void Licensing_shaped_questions_pin_the_faq(string q) => Assert.True(Prompts.IsLicensingQuestion(q));

    [Theory]
    [InlineData("What does the SLOW attack on Trammel do?")]
    [InlineData("What is the capital of France?")]
    [InlineData("How do I set the reverb pre-delay?")]
    public void Other_questions_do_not(string q) => Assert.False(Prompts.IsLicensingQuestion(q));
}
