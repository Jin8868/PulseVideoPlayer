using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PulseVideoPlayer
{
    // v1 仅使用整数、字符串、数组和对象，避免引入额外 JSON 库与反射序列化依赖。
    internal sealed class PulseJsonReader
    {
        private readonly string m_text; // 已经过严格 UTF-8 解码的 JSON。
        private int m_position; // 当前解析位置。

        private PulseJsonReader(string text) { m_text = text; }

        internal static Dictionary<string, object> Parse(string text)
        {
            var reader = new PulseJsonReader(text);
            var result = reader.ReadValue(0) as Dictionary<string, object>;
            reader.SkipWhitespace();
            if (result == null || reader.m_position != text.Length) throw new InvalidDataException("JSON 根对象无效。");
            return result;
        }

        private object ReadValue(int depth)
        {
            // 限制嵌套深度，避免异常元数据耗尽调用栈。
            if (depth > 16) throw new InvalidDataException("JSON 嵌套过深。");
            SkipWhitespace();
            if (m_position >= m_text.Length) throw new InvalidDataException("JSON 内容不完整。");
            switch (m_text[m_position])
            {
                case '{': return ReadObject(depth + 1);
                case '[': return ReadArray(depth + 1);
                case '"': return ReadString();
                case 'n': ReadLiteral("null"); return null;
                case 't': ReadLiteral("true"); return true;
                case 'f': ReadLiteral("false"); return false;
                default: return ReadInteger();
            }
        }

        private Dictionary<string, object> ReadObject(int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            Expect('{');
            if (TryConsume('}')) return result;
            do
            {
                SkipWhitespace();
                string key = ReadString();
                Expect(':');
                if (result.ContainsKey(key)) throw new InvalidDataException("JSON 包含重复字段。");
                result.Add(key, ReadValue(depth));
            } while (TryConsume(','));
            Expect('}');
            return result;
        }

        private List<object> ReadArray(int depth)
        {
            var result = new List<object>();
            Expect('[');
            if (TryConsume(']')) return result;
            do
            {
                if (result.Count >= 64) throw new InvalidDataException("元数据数组过大。");
                result.Add(ReadValue(depth));
            } while (TryConsume(','));
            Expect(']');
            return result;
        }

        private string ReadString()
        {
            Expect('"');
            var result = new StringBuilder();
            while (m_position < m_text.Length)
            {
                char character = m_text[m_position++];
                if (character == '"') return result.ToString();
                if (character < 32) throw new InvalidDataException("JSON 字符串含控制字符。");
                if (character != '\\') { result.Append(character); continue; }
                if (m_position == m_text.Length) throw new InvalidDataException("JSON 转义不完整。");
                char escape = m_text[m_position++];
                switch (escape)
                {
                    case '"': case '\\': case '/': result.Append(escape); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (m_text.Length - m_position < 4) throw new InvalidDataException("Unicode 转义不完整。");
                        if (!ushort.TryParse(m_text.Substring(m_position, 4), NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture, out ushort codePoint))
                        {
                            throw new InvalidDataException("Unicode 转义无效。");
                        }
                        result.Append((char)codePoint);
                        m_position += 4;
                        break;
                    default: throw new InvalidDataException("JSON 转义无效。");
                }
            }
            throw new InvalidDataException("JSON 字符串未结束。");
        }

        private long ReadInteger()
        {
            int start = m_position;
            if (m_text[m_position] == '-') m_position++;
            int digitsStart = m_position;
            while (m_position < m_text.Length && m_text[m_position] >= '0' && m_text[m_position] <= '9') m_position++;
            if (digitsStart == m_position ||
                (m_text[digitsStart] == '0' && m_position - digitsStart > 1) ||
                !long.TryParse(m_text.Substring(start, m_position - start), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out long value))
            {
                throw new InvalidDataException("v1 元数据中的整数无效。");
            }
            return value;
        }

        private void ReadLiteral(string literal)
        {
            if (m_text.Length - m_position < literal.Length ||
                string.CompareOrdinal(m_text, m_position, literal, 0, literal.Length) != 0)
            {
                throw new InvalidDataException("JSON 字面量无效。");
            }
            m_position += literal.Length;
        }

        private void Expect(char character)
        {
            if (!TryConsume(character)) throw new InvalidDataException("JSON 结构无效。");
        }

        private bool TryConsume(char character)
        {
            SkipWhitespace();
            if (m_position >= m_text.Length || m_text[m_position] != character) return false;
            m_position++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (m_position < m_text.Length &&
                (m_text[m_position] == ' ' || m_text[m_position] == '\r' ||
                 m_text[m_position] == '\n' || m_text[m_position] == '\t')) m_position++;
        }
    }
}
