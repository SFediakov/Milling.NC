namespace Miller.Application.Macros;

// A named list of controller lines sent one after the other, DelayMs after each answer.
public sealed record MachineMacro(string Name, int DelayMs, IReadOnlyList<string> Lines);
