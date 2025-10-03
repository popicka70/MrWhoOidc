namespace MrWhoOidc.Auth.Persistence;

public enum QrSessionStatus
{
    Pending = 0,
    Scanned = 1,
    Authenticated = 2,
    Consumed = 3,
    Expired = 4,
    Cancelled = 5
}
