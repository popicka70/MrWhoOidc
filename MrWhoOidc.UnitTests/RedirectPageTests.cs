using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.WebAuth.Pages.Auth;
using Microsoft.AspNetCore.DataProtection;

namespace MrWhoOidc.UnitTests
{
    [TestClass]
    public class RedirectPageTests
    {
        [TestMethod]
        public void OnGet_Rejects_Malicious_Raw_Url()
        {
            var dataProtection = new EphemeralDataProtectionProvider();
            var model = new RedirectModel(dataProtection)
            {
                RedirectUrl = "https://evil.com"
            };

            var result = model.OnGet();

            // Should return BadRequest because unprotect fails
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void OnGet_Accepts_Protected_Url()
        {
            var dataProtection = new EphemeralDataProtectionProvider();
            var protector = dataProtection.CreateProtector("MrWhoOidc.WebAuth.Pages.Auth.Redirect");
            var validUrl = "https://good.com";
            var protectedUrl = protector.Protect(validUrl);

            var model = new RedirectModel(dataProtection)
            {
                RedirectUrl = protectedUrl
            };

            var result = model.OnGet();

            Assert.IsInstanceOfType(result, typeof(PageResult));
            Assert.AreEqual(validUrl, model.RedirectUrl);
        }
    }
}
