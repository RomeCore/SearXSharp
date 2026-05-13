namespace SearXSharp
{
	public class EmptyLogger : ILogger
	{
		public static EmptyLogger Instance { get; } = new EmptyLogger();

		public void Verbose(string template, params object?[] args) { }
		public void Verbose(Exception ex, string template, params object?[] args) { }
		public void Debug(string template, params object?[] args) { }
		public void Debug(Exception ex, string template, params object?[] args) { }
		public void Information(string template, params object?[] args) { }
		public void Warning(string template, params object?[] args) { }
		public void Warning(Exception ex, string template, params object?[] args) { }
		public void Error(string template, params object?[] args) { }
		public void Error(Exception ex, string template, params object?[] args) { }
		public void Fatal(string template, params object?[] args) { }
		public void Fatal(Exception ex, string template, params object?[] args) { }
	}
}