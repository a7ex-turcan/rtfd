using System.Text;
using Rtfd;

// Agents, git and the provider all speak UTF-8. On Windows .NET would use the console's code
// page for redirected streams instead, 437 on an en-US machine, which mangles anything outside
// ASCII. So a redirected stream is UTF-8 whatever the console says; a real console keeps
// .NET's own handling. Same as rtfc.
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var stdout = Console.IsOutputRedirected ? Utf8Writer(Console.OpenStandardOutput()) : Console.Out;
var stderr = Console.IsErrorRedirected ? Utf8Writer(Console.OpenStandardError()) : Console.Error;

return EntryPoint.Run(args, stdout, stderr);

TextWriter Utf8Writer(Stream stream) => TextWriter.Synchronized(new StreamWriter(stream, utf8) { AutoFlush = true });
