using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SearXSharp
{
	public interface ILogger
	{
		void Verbose(string template, params object?[] args);
		void Verbose(Exception ex, string template, params object?[] args);
		void Debug(string template, params object?[] args);
		void Debug(Exception ex, string template, params object?[] args);
		void Information(string template, params object?[] args);
		void Warning(string template, params object?[] args);
		void Warning(Exception ex, string template, params object?[] args);
		void Error(string template, params object?[] args);
		void Error(Exception ex, string template, params object?[] args);
		void Fatal(string template, params object?[] args);
		void Fatal(Exception ex, string template, params object?[] args);
	}
}