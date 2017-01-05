﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView
{
    internal class GremlinValueVariable : GremlinTableVariable
    {
        public GremlinVariableProperty ProjectVariable { get; set; }

        public GremlinValueVariable(GremlinVariableProperty projectVariable)
        {
            ProjectVariable = projectVariable;
        }

        internal override GremlinScalarVariable DefaultProjection()
        {
            return new GremlinVariableProperty(this, "_value");
        }

        public override WTableReference ToTableReference()
        {
            List<WScalarExpression> parameters = new List<WScalarExpression>();
            parameters.Add(ProjectVariable.ToScalarExpression());
            var secondTableRef = GremlinUtil.GetFunctionTableReference("value", parameters, VariableName);
            return GremlinUtil.GetCrossApplyTableReference(null, secondTableRef);
        }
    }
}
