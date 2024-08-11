﻿using System.IO;
using System.Text;

namespace FastCsv;

public class CsvValidator
{
    private readonly char _quote = '\"';
    public ValidationResult Validate(Stream content, ValidationOptions options) 
    {
        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();

        bool onHeaderRow = options.HasHeaderRow;
        int rowCount = 0;
        int initialFieldCount = -1;
        bool containsLineBreakInQuotedField = false;
        string previousLine = string.Empty;

        List<ValidationMessage> validationMessages = new List<ValidationMessage>();
        List<string> headers = new List<string>();

        using (StreamReader streamReader = new StreamReader(content))
        {
            while (!streamReader.EndOfStream)
            {
                string? line = streamReader.ReadLine();
                if (line == null) break; // no content found, just end

                if (containsLineBreakInQuotedField && !string.IsNullOrEmpty(previousLine))
                {
                    // we have content from a previous line to concatenate...
                    line = previousLine + "\r\n" + line;
                }
                
                ReadOnlySpan<char> row = line.AsSpan();
                
                if (onHeaderRow)
                {
                    // TODO: Check column names against schema
                }
                
                ProcessRowResult processRowResult = ProcessRow(row, rowCount, options, onHeaderRow);
                validationMessages.AddRange(processRowResult.Messages);
                containsLineBreakInQuotedField = processRowResult.ContainsLineBreakInQuotedField;

                if (onHeaderRow)
                {
                    headers.AddRange(processRowResult.HeaderRowNames);
                }
                
                onHeaderRow = false;

                int rowFieldCount = -1;
                
                if (initialFieldCount == -1)
                {
                    initialFieldCount = processRowResult.FieldCount;
                }
                else
                {
                    rowFieldCount = processRowResult.FieldCount;
                }

                if (containsLineBreakInQuotedField)
                {
                    previousLine = line;
                    continue;
                }
                
                rowCount++;

                if (initialFieldCount != -1 && rowFieldCount != -1 && rowFieldCount != initialFieldCount)
                {
                    // ERROR - RFC 4180 - field counts mismatch
                    var errorMessage = new ValidationMessage()
                    {
                        Code = 1,
                        Severity = Severity.Error,
                        Content = $"{rowFieldCount} detected; mismatch with first row's field count of {initialFieldCount}",
                        MessageType = ValidationMessageType.Structural,
                        Row = rowCount,
                        FieldNumber = -1,
                        FieldName = string.Empty,
                        Character = -1
                    };
                    validationMessages.Add(errorMessage);
                }
            }
        }

        sw.Stop();

        var result = new ValidationResult()
        {
            ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
            DataRowCount = options.HasHeaderRow ? rowCount - 1 : rowCount,
            FieldCount = initialFieldCount
        };

        for (int i = 0; i < headers.Count; i++)
        {
            string header = headers[i];
            if (string.IsNullOrEmpty(header))
            {
                // WARNING - RFC 4180 - empty field name
                var warningMessage = new ValidationMessage()
                {
                    Code = 4,
                    Severity = Severity.Warning,
                    Content = $"A field name with a length of 0 characters was detected",
                    MessageType = ValidationMessageType.Structural,
                    Row = 1,
                    FieldNumber = i + 1,
                    FieldName = string.Empty,
                    Character = -1
                };
                validationMessages.Add(warningMessage);
            }
            else if (string.IsNullOrWhiteSpace(header))
            {
                // INFO - RFC 4180 - field name with just whitespace
                var infoMessage = new ValidationMessage()
                {
                    Code = 5,
                    Severity = Severity.Information,
                    Content = $"A field name with only whitespace characters was detected",
                    MessageType = ValidationMessageType.Structural,
                    Row = 1,
                    FieldNumber = i + 1,
                    FieldName = string.Empty,
                    Character = -1
                };
                validationMessages.Add(infoMessage);
            }
            result.AddHeader(header);
        }
        
        foreach (var message in validationMessages)
        {
            result.AddMessage(message);
        }

        return result;
    }

    private ProcessRowResult ProcessRow(ReadOnlySpan<char> row, int dataRowCount, ValidationOptions options, bool isHeaderRow)
    {
        List<ValidationMessage> validationMessages = new();

        List<string>? headerRowNames = isHeaderRow ? new List<string>() : null;

        bool inQuotedField = false;
        bool isEscapedQuote = false;
        int fieldCount = 1;
        int previousCommaPosition = 0;

        bool lineBreakInQuotedField = false;

        if (isHeaderRow && row.Length == 0)
        {
            // special case - header row with zero length, so assume there is just one field name and make it an empty string
            headerRowNames?.Add(string.Empty);
        }

        for (int i = 0; i < row.Length; i++)
        {
            // get the current character
            ReadOnlySpan<char> currentCharacter = row.Slice(start: i, length: 1);
            ReadOnlySpan<char> previousCharacter = i == 0 ? null : row.Slice(start: i - 1, length: 1);
            ReadOnlySpan<char> nextCharacter = i == row.Length - 1 ? null : row.Slice(start: i + 1, length: 1);

            if (inQuotedField == false && currentCharacter[0] == _quote && (previousCharacter == null || previousCharacter[0] == options.Separator))
            {
                // Happy path. We've come upon a quote that is preceded by a seperator or null (new row) - we're now going to be processing all remaining characters as if they're in a quoted field
                inQuotedField = true;
            }
            else if (inQuotedField == true && currentCharacter[0] == _quote && (nextCharacter == null || nextCharacter[0] == options.Separator))
            {
                // Happy path. We've come upon a quote that ends with either a new line, or a separator, so we stop processing all remaining characters as if they're in a quoted field
                inQuotedField = false;
            }
            else if (inQuotedField == true
                     && isEscapedQuote == false
                     && currentCharacter[0] == _quote
                     && (nextCharacter != null && nextCharacter[0] != options.Separator))
            {
                // We're in a quoted field, and we came across a quote.
                isEscapedQuote = true;
            }
            else if (inQuotedField == true
                     && isEscapedQuote == true
                     && currentCharacter[0] == _quote
                     && (previousCharacter != null && previousCharacter[0] == _quote))
            {
                // We're in a quoted field, and we came across a second quote in a row.
                isEscapedQuote = false;
            }
            else if (inQuotedField == true
                     && isEscapedQuote == true
                     && currentCharacter[0] != _quote)
            {
                // TODO: Throw an ERROR - RFC-4180
                var errorMessage = new ValidationMessage()
                {
                    Code = 2,
                    Severity = Severity.Error,
                    Content = $"Unescaped quote detected in a quoted field",
                    MessageType = ValidationMessageType.Structural,
                    Row = dataRowCount,
                    FieldNumber = fieldCount,
                    FieldName = string.Empty,
                    Character = i
                };
                validationMessages.Add(errorMessage);

                isEscapedQuote = false;
            }
            else if (inQuotedField == false && currentCharacter[0] == _quote &&
                     (previousCharacter != null && previousCharacter[0] != options.Separator))
            {
                /* Unhappy path. We've come upon a quote that occurred outside a quoted string, but where the
                 prior character is neither null nor a separator. This indicates an invalid use of quotes.
                 Example: NA"ME,AGE,DOB
                 */
                var errorMessage = new ValidationMessage()
                {
                    Code = 2,
                    Severity = Severity.Error,
                    Content = $"Quote detected outside of a quoted string",
                    MessageType = ValidationMessageType.Structural,
                    Row = dataRowCount,
                    FieldNumber = fieldCount,
                    FieldName = string.Empty,
                    Character = i
                };
                validationMessages.Add(errorMessage);
            }
            else if (inQuotedField == true && isEscapedQuote == false && nextCharacter == null)
            {
                // we're in a quoted field and came across a line break - we need to do a continuation
                lineBreakInQuotedField = true;
            }

            if (!inQuotedField)
            {
                if (currentCharacter[0] == options.Separator)
                {
                    fieldCount++;
                    if (isHeaderRow)
                    {
                        int charsToTake = i - previousCommaPosition;
                        headerRowNames?.Add(row.Slice(start: previousCommaPosition, length: charsToTake).ToString());
                        previousCommaPosition = i + 1;
                    }
                }
                
                // the following code ensures that we get the final column name
                if (isHeaderRow && i == row.Length - 1)
                {
                    int charsToTake = i - previousCommaPosition + 1;
                    headerRowNames?.Add(row.Slice(start: previousCommaPosition, length: charsToTake).ToString());
                }
            }
        }

        var result = new ProcessRowResult()
        {
            FieldCount = fieldCount,
            ContainsLineBreakInQuotedField = lineBreakInQuotedField,
            Line = lineBreakInQuotedField ? row.ToString() : string.Empty
        };

        foreach (var message in validationMessages)
        {
            result.AddMessage(message);
        }

        if (isHeaderRow && headerRowNames != null)
        {
            foreach (string headerRowName in headerRowNames)
            {
                result.AddHeaderRowName(headerRowName);
            }
        }

        return result;
    }
}
