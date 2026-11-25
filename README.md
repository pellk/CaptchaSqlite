# nuel_CaptchaSqlite

A fast, lightweight, and thread-safe .NET captcha library with cached typography, SixLabors ImageSharp rendering, and SQLite storage.

## Features
- **Task-based API**: Storage methods accept a `CancellationToken`. The SQLite provider executes database I/O synchronously.
- **Cached Typography**: Font families and glyph collections are loaded and cached once at startup, eliminating per-request disk reads.
- **Dependency Injection**: Seamless integration via standard ASP.NET Core DI extensions.
- **Storage Decoupling**: Pluggable storage via `ICaptchaStore` (with built-in `SqliteCaptchaStore`).
- **Low-Resource Friendly**: Housekeeping uses opportunistic, throttled cleanup without requiring background worker processes that may get killed on shared hosting or low-resource hosts.

---

## Getting Started

```sh
dotnet add package nuel_CaptchaSqlite --version 1.0.0
```

### 1. Register Services (`Program.cs`)
```csharp
builder.Services.AddCaptcha(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("Captcha");
    options.FontPath = Path.Combine(builder.Environment.WebRootPath, "fonts/Roboto.ttf");
    
    // Optional settings:
    options.Digits = 5;                              // default: 5 (supported: 1-9)
    options.Width = 200;                             // default: 200
    options.Height = 60;                             // default: 60
    options.FontSize = 30f;                          // default: 30
    options.ForeColor = SixLabors.ImageSharp.Color.FromPixel(new SixLabors.ImageSharp.PixelFormats.Rgb24(60, 60, 60));
    options.BackColor = SixLabors.ImageSharp.Color.White;
    options.Expiration = TimeSpan.FromMinutes(5);    // default: 5 minutes
    options.CleanUpInterval = TimeSpan.FromMinutes(10); // default: 10 minutes
});
```

Set the SQLite connection string in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Captcha": "Data Source=captcha.db"
  }
}
```

Use a writable database location outside the web root. Relative paths resolve from the process's working directory. The database's parent directory must already exist.

You can also set the connection string with `builder.Services.AddSqliteCaptchaStore(connectionString)` alongside `AddCaptcha()`.

---

### 2. Generate Captcha in Controller or Minimal API

#### Controller Example:
```csharp
public class HomeController : Controller
{
    private readonly ICaptchaService _captchaService;

    public HomeController(ICaptchaService captchaService)
    {
        _captchaService = captchaService;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var captcha = await _captchaService.GenerateAsync(ct);
        ViewBag.Src = captcha.Src;
        ViewBag.Code = captcha.Id;
        return View();
    }
}
```

#### Razor View (`Index.cshtml`):
```html
<form method="post" action="/submit">
    <input type="hidden" name="CaptchaCode" value="@ViewBag.Code" />
    <img src="@ViewBag.Src" alt="Captcha" />
    <input type="text" name="CaptchaUserInput" autocomplete="off" required />
    <button type="submit">Verify</button>
</form>
```

#### Validating Captcha:
```csharp
[HttpPost("/submit")]
public async Task<IActionResult> Submit(
    [FromForm] string captchaCode, 
    [FromForm] string captchaUserInput, 
    CancellationToken ct)
{
    bool isValid = await _captchaService.ValidateAsync(captchaCode, captchaUserInput, ct);
    if (!isValid)
    {
        ModelState.AddModelError("CaptchaUserInput", "Invalid or expired captcha.");
        return View();
    }

    // Process submission...
    return RedirectToAction("Success");
}
```

---

## Database Setup & Housekeeping

- **Automatic Setup**: By default the provider opens or creates the SQLite file, and the store creates `CaptchaCodes` and `IX_CaptchaCodes_ExpiresAt` on first use. Set `AutoCreateDatabaseTable = false` if you provision the schema separately. `SqliteCaptchaStore.EnsureDatabaseInitializedAsync()` can also create the schema explicitly.
- **Atomic Validation**: A correct answer to an unexpired challenge consumes it in a single SQLite statement. Each challenge's expiration is set when it is stored.
- **Opportunistic Housekeeping**: After storing a challenge, the store can schedule background cleanup of expired records. `CleanUpInterval` limits how often this runs. To trigger cleanup manually, call:
  ```csharp
  await captchaService.CleanUpAsync(cancellationToken: ct);
  ```
