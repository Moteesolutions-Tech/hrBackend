namespace Motee.Application.Common;

// "This is a developer's machine, so shortcuts that would be dangerous on a server are
// acceptable." One definition, consulted by everything that needs it, rather than each
// feature deciding for itself what counts as a safe environment - which is how two
// checks for the same idea end up disagreeing.
//
// Everything behind this is a shortcut around a security control, so treat adding a new
// consumer as a security decision rather than a convenience.
public interface IDebugMode
{
    bool Enabled { get; }
}
