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
    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
