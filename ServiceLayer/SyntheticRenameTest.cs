// Синтетический тест для roslyn_rename: 5 вхождений SyntheticRenameTarget.
namespace RoslynMcp.ServiceLayer;

public static class SyntheticRenameTest
{
    public static int Use(SyntheticRenameTarget a, SyntheticRenameTarget b)
    {
        SyntheticRenameTarget c = a;
        return SyntheticRenameTarget.StaticValue + c.Value;
    }
}

public class SyntheticRenameTarget
{
    public static int StaticValue => 1;
    public int Value { get; } = 2;
}

/// <summary>Пример для теста roslyn_resolve_breakpoint: конструктор и индексатор.</summary>
public class BreakpointResolveSample
{
    private int _value;

    public BreakpointResolveSample()
    {
        _value = 0;
    }

    public BreakpointResolveSample(int initial)
    {
        _value = initial;
    }

    public int this[int index]
    {
        get => _value + index;
        set => _value = value;
    }
}
