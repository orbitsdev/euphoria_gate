using System;

public static class HikvisionErrorHelper
{
    public static string GetErrorMessage(uint errorCode)
    {
        return errorCode switch
        {
            1 => "Username not correct",
            7 => "Password error",
            11 => "IP connection failed",
            29 => "Connection timeout",
            113 => "No route to host",
            _ => $"Unknown error code {errorCode}"
        };
    }
}
