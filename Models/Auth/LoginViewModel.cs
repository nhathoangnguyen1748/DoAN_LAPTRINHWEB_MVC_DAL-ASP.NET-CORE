namespace DoAnLapTrinhWeb.Models.Auth;

public class LoginViewModel
{
    public string ReturnUrl { get; set; } = "/";
    public bool IsGoogleConfigured { get; set; }
    public string? Message { get; set; }
}
