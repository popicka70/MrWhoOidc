using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.WebAuth.Pages.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.UnitTests
{
    [TestClass]
    public class RedirectPageTests
    {
        [TestMethod]
        public void OnGet_Rejects_Malicious_Raw_Url()
        {
            var model = CreateModel("https://evil.com");

            var result = model.OnGet();

            // Should return BadRequest because unprotect fails
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
            AssertHeaders(model);
        }

        [TestMethod]
        public void OnGet_Accepts_Protected_Url()
        {
            var dataProtection = new EphemeralDataProtectionProvider();
            var protector = dataProtection.CreateProtector("MrWhoOidc.WebAuth.Pages.Auth.Redirect");
            var validUrl = "https://good.com";
            var protectedUrl = protector.Protect(validUrl);

            var model = CreateModel(protectedUrl, dataProtection);

            var result = model.OnGet();

            Assert.IsInstanceOfType(result, typeof(PageResult));
            Assert.AreEqual(validUrl, model.RedirectUrl);
            AssertHeaders(model);
        }

        private static RedirectModel CreateModel(string redirectUrl, IDataProtectionProvider? dataProtection = null)
        {
            var model = new RedirectModel(dataProtection ?? new EphemeralDataProtectionProvider())
            {
                RedirectUrl = redirectUrl,
                PageContext = new PageContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            return model;
        }

        private static void AssertHeaders(RedirectModel model)
        {
            var headers = model.HttpContext.Response.Headers;
            Assert.AreEqual("no-store, no-cache, max-age=0", headers.CacheControl.ToString());
            Assert.AreEqual("no-cache", headers.Pragma.ToString());
        }
    }
}
