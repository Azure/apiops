using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace common
{
    public abstract record DefaultPolicyXmlSpecification
    {
        public abstract string Format { get; }
        public record RawXmlFormat : DefaultPolicyXmlSpecification
        {
            public override string Format => "rawxml";
        }
        public record XmlFormat : DefaultPolicyXmlSpecification
        {
            public override string Format => "xml";
        }
    }
}
