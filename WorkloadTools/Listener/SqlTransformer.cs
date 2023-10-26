﻿using Microsoft.SqlServer.Management.SqlParser.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WorkloadTools.Consumer.Analysis;

namespace WorkloadTools.Listener
{
    public class SqlTransformer
    {

        private static readonly Regex _execPrepped = new Regex("^EXEC\\s+SP_EXECUTE\\s+(?<stmtnum>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _execUnprep = new Regex("EXEC\\s+SP_UNPREPARE\\s+(?<stmtnum>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _prepareSql = new Regex("EXEC\\s+(?<preptype>SP_PREP(ARE|EXEC))\\s+@P1\\s+OUTPUT,\\s*(NULL|(N\\'.*?\\')),\\s*N(?<remaining>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex _preppedSqlStatement = new Regex("^(')(?<statement>((?!\\1).|\\1{2})*)\\1", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex _doubleApostrophe = new Regex("('')(?<string>.*?)('')", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant);

        private static readonly MatchEvaluator decimal38Evaluator = new MatchEvaluator(MakeFloat);

        private static string MakeFloat(Match match)
        {
            if (match.Value.EndsWith("E0"))
            {
                return match.Value;
            }
            else
            {
                return match.Value + "E0";
            }
        }

        public string Transform(string command)
        {
            // remove the handle from the sp_prepexec call
            if (command.Contains("sp_prepexec "))
            {
                command = RemoveFirstP1(command, out _);
                if (!command.EndsWith("EXEC sp_unprepare @p1;"))
                {
                    command += " ; EXEC sp_unprepare @p1;";
                }
            }

            //  remove the handle from the sp_cursoropen call
            else if (command.Contains("sp_cursoropen "))
            {
                command = RemoveFirstP1(command, out _);
                if (!command.EndsWith("EXEC sp_cursorclose @p1;"))
                {
                    command += " ; EXEC sp_cursorclose @p1;";
                }
            }

            //  remove the handle from the sp_cursorprepexec call
            else if (command.Contains("sp_cursorprepexec "))
            {
                command = RemoveFirstP1(command, out _);
                if (!command.EndsWith("EXEC sp_cursorunprepare @p1;"))
                {
                    command += " ; EXEC sp_cursorunprepare @p1;";
                }
            }

            // trim numbers with precision > 38
            // rpc_completed events may return float parameters
            // as long numeric strings that exceed the maximum decimal
            // precision of 38. 
            // Any decimal numeric string in T-SQL is interpreted as decimal,
            // unless it ends with "E0", which designates a float literal. 
            // Any decimal numeric string longer than 38 characters needs to
            // be appended "E0" to be treated as float.
            //
            // Unfortunately RegExs are evil and also match numbers
            // that already have their "E0" appended, so I need to append
            // only when not found
            //
            // RegEx: \b([0-9\.]{38,})+([E]+[0]+)?\b
            // \b                 means "word boundary", including whitespace, punctuation or begin/end input
            // ([0-9\.]{38,})+    means "numbers or . repeated at least 38 times"
            // ([E]+[0]+)?        means "E0" zero or one time
            // \b                 means word boundary again
            command = Regex.Replace(command, @"\b([0-9\.]{38,})+([E]+[0]+)?\b", decimal38Evaluator);

            return command;
        }

        public bool Skip(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                return true;
            }
            // skip reset connection commands
            //if (command.Contains("sp_reset_connection"))
            //    return true;

            // skip unprepare commands
            //if (command.Contains("sp_unprepare "))
            //    return true;

            // skip cursor fetch
            if (command.Contains("sp_cursor "))
            {
                return true;
            }

            // skip cursor fetch
            if (command.Contains("sp_cursorfetch "))
            {
                return true;
            }

            // skip cursor close
            if (command.Contains("sp_cursorclose "))
            {
                return true;
            }

            // skip cursor option
            if (command.Contains("sp_cursoroption "))
            {
                return true;
            }

            // skip cursor unprepare
            if (command.Contains("sp_cursorunprepare "))
            {
                return true;
            }

            // skip internal commands
            if (command.Contains("fn_xe_file_target_read_file")
                ||
                command.Contains("ALTER EVENT SESSION")
                ||
                command.Contains("fn_trace_getinfo"))
            {
                return true;
            }

            // skip KILL commands
            if (command.StartsWith("KILL"))
            {
                return true;
            }

            // skip BULK INSERT commands
            if (command.StartsWith("insert bulk"))
            {
                return true;
            }

            // skip sp_execute
            //if (command.Contains("sp_execute "))
            //    return true;

            return false;
        }

        private string RemoveFirstP1(string command, out string originalP1)
        {
            var idx = command.IndexOf("set @p1=");
            originalP1 = null;
            if (idx > 0)
            {
                originalP1 = "";
                var sb = new StringBuilder(command);
                idx += 8; // move past "set @p1="

                // replace numeric chars with 0s
                while (char.IsNumber(sb[idx]))
                {
                    originalP1 += sb[idx];
                    sb[idx] = '0';
                    idx++;
                }
                command = sb.ToString();
            }
            return command;
        }

        private string RemoveFirstPrepStatementNum(string command, out string originalStmtNum)
        {
            var idx = command.IndexOf(" sp_execute ");
            originalStmtNum = null;
            if (idx > 0)
            {
                originalStmtNum = "";
                var sb = new StringBuilder(command);
                idx += 12; // move past " sp_execute "

                // replace numeric chars with §
                var iter = 0;
                var initialIdx = idx;
                while (idx < sb.Length && char.IsNumber(sb[idx]))
                {
                    originalStmtNum += sb[idx];
                    if(iter == 0)
                    {
                        sb[idx] = '§';
                    }
                    else
                    {
                        sb[idx] = ' ';
                    }
                    idx++;
                    iter++;
                }
                // remove extra characters after the newly added § symbol
                if(initialIdx + 1 < sb.Length && iter > 1)
                {
                    _ = sb.Remove(initialIdx + 1, iter - 1);
                }
                command = sb.ToString();
            }
            return command;
        }

        public NormalizedSqlText Normalize(string command)
        {
            var result = new NormalizedSqlText(command);

            var num = 0;

            if (command.Contains("sp_reset_connection"))
            {
                if (command.Contains("Nonpooled"))
                {
                    result.CommandType = NormalizedSqlText.CommandTypeEnum.SP_RESET_CONNECTION_NONPOOLED;
                }
                else
                {
                    result.CommandType = NormalizedSqlText.CommandTypeEnum.SP_RESET_CONNECTION;
                }
                return result;
            }                

            var match3 = _prepareSql.Match(command);
            if (match3.Success)
            {
                if (match3.Groups["preptype"].ToString().ToLower() == "sp_prepare")
                {
                    if(match3.Groups["stmtnum"].Success)
                    {
                        num = !(match3.Groups["stmtnum"].ToString() == "NULL") ? Convert.ToInt32(match3.Groups["stmtnum"].ToString()) : 0;
                    }

                    var sql = match3.Groups["remaining"].ToString();
                    var match4 = _preppedSqlStatement.Match(sql);
                    if (match4.Success)
                    {
                        sql = match4.Groups["statement"].ToString();
                        sql = _doubleApostrophe.Replace(sql, "'${string}'");
                        result.Statement = sql;
                        result.NormalizedText = RemoveFirstP1(result.OriginalText, out var originalHandle);
                        if (int.TryParse(originalHandle, out var n))
                        {
                            result.Handle = n;
                        }
                        else
                        {
                            result.Handle = num;
                        }
                        result.CommandType = NormalizedSqlText.CommandTypeEnum.SP_PREPARE;
                    }
                }
                return result;
            }

            var match5 = _execPrepped.Match(command);
            if (match5.Success)
            {
                num = Convert.ToInt32(match5.Groups["stmtnum"].ToString());
                result.Handle = num;
                var textWithPlaceHolder = RemoveFirstPrepStatementNum(result.Statement, out var originalHandle);
                if (int.TryParse(originalHandle, out var n))
                {
                    result.Handle = n;
                }
                else
                {
                    result.Handle = num;
                }
                result.Statement = textWithPlaceHolder;
                result.NormalizedText = textWithPlaceHolder;
                result.CommandType = NormalizedSqlText.CommandTypeEnum.SP_EXECUTE;
                return result;
            }

            var match6 = _execUnprep.Match(command);
            if (match6.Success)
            {
                num = Convert.ToInt32(match6.Groups["stmtnum"].ToString());
                result.Handle = num;
                result.Statement = "EXEC sp_unprepare §";
                result.NormalizedText = "EXEC sp_unprepare §";
                result.CommandType = NormalizedSqlText.CommandTypeEnum.SP_UNPREPARE;
                return result;
            }

            return result;
        }

    }
}
