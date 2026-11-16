using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using C_LabTap.Models;

namespace C_LabTap.Controllers;

// Stays public so the exception handler's error page (and the default
// Index) render for anyone, instead of an unauthenticated error bouncing
// through the global AuthorizeFilter into a login redirect that hides
// what actually went wrong.
[AllowAnonymous]
public class HomeController : Controller
{
    // Index()/Privacy() were the dotnet-new-mvc scaffolding pages ("Welcome
    // / Learn about building Web apps") - removed, the only thing this
    // controller still serves is the error page below.

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
