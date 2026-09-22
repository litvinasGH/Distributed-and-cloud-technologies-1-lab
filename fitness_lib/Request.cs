namespace fitness_lib;


public class Request
{
    public string Operation { get; set; } = string.Empty;

    public string? Data { get; set; }
}

public class Response
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public object? Data { get; set; }
}