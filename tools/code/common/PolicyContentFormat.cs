using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace common
{
    public abstract record PolicyContentFormat
    {
        private static readonly Lazy<PolicyContentFormat> instance = new Lazy<PolicyContentFormat>(() => new RawXml());

        public static PolicyContentFormat Default => instance.Value;

        public sealed record RawXml : PolicyContentFormat;
        public sealed record Xml : PolicyContentFormat;
        public sealed record RawXmlLink : PolicyContentFormat;
        public sealed record XmlLink : PolicyContentFormat;


        public string GetPolicyContentFormat
        {
            get
            {
                return this switch
                {
                    PolicyContentFormat.RawXml => "rawxml",
                    PolicyContentFormat.Xml => "xml",
                    PolicyContentFormat.RawXmlLink => "rawxml-link",
                    PolicyContentFormat.XmlLink => "xml-link",
                    _ => throw new NotSupportedException()
                };
            }
        }
    }
}
