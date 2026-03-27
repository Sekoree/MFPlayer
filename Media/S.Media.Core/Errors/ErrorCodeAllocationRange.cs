namespace S.Media.Core.Errors;

public readonly record struct ErrorCodeAllocationRange(int Start, int End, string Owner)
{
    public bool Contains(int code) => code >= Start && code <= End;
}
