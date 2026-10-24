namespace WhatDidIDo.Tray.Core.Control;

/// <summary>
/// A failed control request. <see cref="NotRunning"/>: no control file, or
/// nothing listening at its base_url. <see cref="Status"/>/<see cref="Code"/>:
/// the HTTP status and the API's <c>{"error": code}</c>. Messages never
/// contain the token.
/// </summary>
public sealed class ControlException : Exception
{
    public ControlException(string message, int status = 0, string? code = null, bool notRunning = false)
        : base(message)
    {
        Status = status;
        Code = code;
        NotRunning = notRunning;
    }

    public ControlException() : this("control request failed") { }

    public ControlException(string message, Exception innerException) : base(message, innerException) { }

    public int Status { get; }
    public string? Code { get; }
    public bool NotRunning { get; }
}
