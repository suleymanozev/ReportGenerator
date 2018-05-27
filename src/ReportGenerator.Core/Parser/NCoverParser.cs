﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Palmmedia.ReportGenerator.Core.Logging;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using Palmmedia.ReportGenerator.Core.Properties;

namespace Palmmedia.ReportGenerator.Core.Parser
{
    /// <summary>
    /// Parser for XML reports generated by NCover.
    /// </summary>
    internal class NCoverParser : ParserBase
    {
        /// <summary>
        /// The Logger.
        /// </summary>
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(NCoverParser));

        /// <summary>
        /// Regex to analyze if a method name belongs to a lamda expression.
        /// </summary>
        private static Regex lambdaMethodNameRegex = new Regex("<.+>.+__.+", RegexOptions.Compiled);

        /// <summary>
        /// Parses the given XML report.
        /// </summary>
        /// <param name="report">The XML report</param>
        /// <returns>The parser result.</returns>
        public override ParserResult Parse(XContainer report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var assemblies = new ConcurrentBag<Assembly>();

            var modules = report.Descendants("module").ToArray();

            var assemblyNames = modules
                .Select(module => module.Attribute("assembly").Value)
                .Distinct()
                .OrderBy(a => a)
                .ToArray();

            Parallel.ForEach(assemblyNames, assemblyName => assemblies.Add(ProcessAssembly(modules, assemblyName)));

            var result = new ParserResult(assemblies.OrderBy(a => a.Name).ToList(), false, this.ToString());
            return result;
        }

        /// <summary>
        /// Processes the given assembly.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns>The <see cref="Assembly"/>.</returns>
        private static Assembly ProcessAssembly(XElement[] modules, string assemblyName)
        {
            Logger.DebugFormat("  " + Resources.CurrentAssembly, assemblyName);

            var classNames = modules
                .Where(module => module.Attribute("assembly").Value.Equals(assemblyName))
                .Elements("method")
                .Where(m => m.Attribute("excluded").Value == "false")
                .Select(method => method.Attribute("class").Value)
                .Where(value => !value.Contains("__") && !value.Contains("+"))
                .Distinct()
                .OrderBy(name => name)
                .ToArray();

            var assembly = new Assembly(assemblyName);

            Parallel.ForEach(classNames, className => assembly.AddClass(ProcessClass(modules, assembly, className)));

            return assembly;
        }

        /// <summary>
        /// Processes the given class.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="assembly">The assembly.</param>
        /// <param name="className">Name of the class.</param>
        /// <returns>The <see cref="Class"/>.</returns>
        private static Class ProcessClass(XElement[] modules, Assembly assembly, string className)
        {
            var filesOfClass = modules
                .Where(module => module.Attribute("assembly").Value.Equals(assembly.Name)).Elements("method")
                .Where(method => method.Attribute("class").Value.Equals(className))
                .Where(m => m.Attribute("excluded").Value == "false")
                .Elements("seqpnt")
                .Select(seqpnt => seqpnt.Attribute("document").Value)
                .Distinct()
                .ToArray();

            var @class = new Class(className, assembly);

            foreach (var file in filesOfClass)
            {
                @class.AddFile(ProcessFile(modules, @class, file));
            }

            return @class;
        }

        /// <summary>
        /// Processes the file.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="class">The class.</param>
        /// <param name="filePath">The file path.</param>
        /// <returns>The <see cref="CodeFile"/>.</returns>
        private static CodeFile ProcessFile(XElement[] modules, Class @class, string filePath)
        {
            var methodsOfClass = modules
                .Where(type => type.Attribute("assembly").Value.Equals(@class.Assembly.Name))
                .Elements("method")
                .Where(m => m.Attribute("excluded").Value == "false")
                .Where(method => method.Attribute("class").Value.StartsWith(@class.Name, StringComparison.Ordinal))
                .ToArray();

            var seqpntsOfFile = methodsOfClass.Elements("seqpnt")
                .Where(seqpnt => seqpnt.Attribute("document").Value.Equals(filePath) && seqpnt.Attribute("line").Value != "16707566")
                .Select(seqpnt => new
                {
                    LineNumberStart = int.Parse(seqpnt.Attribute("line").Value, CultureInfo.InvariantCulture),
                    LineNumberEnd = int.Parse(seqpnt.Attribute("endline").Value, CultureInfo.InvariantCulture),
                    Visits = int.Parse(seqpnt.Attribute("visitcount").Value, CultureInfo.InvariantCulture)
                })
                .OrderBy(seqpnt => seqpnt.LineNumberEnd)
                .ToArray();

            int[] coverage = new int[] { };
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[] { };

            if (seqpntsOfFile.Length > 0)
            {
                coverage = new int[seqpntsOfFile[seqpntsOfFile.LongLength - 1].LineNumberEnd + 1];
                lineVisitStatus = new LineVisitStatus[seqpntsOfFile[seqpntsOfFile.LongLength - 1].LineNumberEnd + 1];

                for (int i = 0; i < coverage.Length; i++)
                {
                    coverage[i] = -1;
                }

                foreach (var seqpnt in seqpntsOfFile)
                {
                    for (int lineNumber = seqpnt.LineNumberStart; lineNumber <= seqpnt.LineNumberEnd; lineNumber++)
                    {
                        coverage[lineNumber] = coverage[lineNumber] == -1 ? seqpnt.Visits : coverage[lineNumber] + seqpnt.Visits;
                        lineVisitStatus[lineNumber] = lineVisitStatus[lineNumber] == LineVisitStatus.Covered || seqpnt.Visits > 0 ? LineVisitStatus.Covered : LineVisitStatus.NotCovered;
                    }
                }
            }

            var codeFile = new CodeFile(filePath, coverage, lineVisitStatus);

            SetCodeElements(codeFile, methodsOfClass);

            return codeFile;
        }

        /// <summary>
        /// Extracts the methods/properties of the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="methodsOfClass">The methods of the class.</param>
        private static void SetCodeElements(CodeFile codeFile, IEnumerable<XElement> methodsOfClass)
        {
            foreach (var method in methodsOfClass)
            {
                string methodName = method.Attribute("name").Value;

                if (lambdaMethodNameRegex.IsMatch(methodName))
                {
                    continue;
                }

                CodeElementType type = CodeElementType.Method;

                if (methodName.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
                    || methodName.StartsWith("set_", StringComparison.OrdinalIgnoreCase))
                {
                    type = CodeElementType.Property;
                    methodName = methodName.Substring(4);
                }

                var seqpnt = method
                    .Elements("seqpnt")
                    .FirstOrDefault();

                if (seqpnt != null && seqpnt.Attribute("document").Value.Equals(codeFile.Path))
                {
                    int line = int.Parse(seqpnt.Attribute("line").Value, CultureInfo.InvariantCulture);
                    codeFile.AddCodeElement(new CodeElement(methodName, type, line));
                }
            }
        }
    }
}
