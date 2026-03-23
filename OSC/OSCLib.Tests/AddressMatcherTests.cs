using OSCLib;
using Xunit;

namespace OSCLib.Tests;

public class AddressMatcherTests
{
    [Theory]
    [InlineData("/foo/*", "/foo/bar", true)]
    [InlineData("/foo/?ar", "/foo/bar", true)]
    [InlineData("/foo/{bar,baz}", "/foo/baz", true)]
    [InlineData("/foo/[a-c]ar", "/foo/bar", true)]
    [InlineData("/foo/[!a]ar", "/foo/bar", true)]
    [InlineData("/root//leaf", "/root/a/b/leaf", true)]
    [InlineData("/root//leaf", "/root/leaf", true)]
    [InlineData("/root//leaf", "/root/a/b/stem", false)]
    public void Matches_Expected_Patterns(string pattern, string address, bool expected)
    {
        Assert.Equal(expected, OSCAddressMatcher.IsMatch(pattern, address));
    }
}

