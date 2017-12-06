﻿#define USE_SERIALIZE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GraphView.GraphViewDBPortal;

namespace GraphView
{
    partial class WSelectQueryBlock
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            // Optimal tranversal orders
            TraversalOrder traversalOrder = new TraversalOrder();

            // Construct AggregationBlocks and Create MatchGraph
            GlobalDependencyVisitor gdVisitor = new GlobalDependencyVisitor();
            gdVisitor.Invoke(this.FromClause, this.MatchClause);
            List<AggregationBlock> aggregationBlocks = gdVisitor.blocks;
            HashSet<string> vertexAndEdgeAliases = new HashSet<string>();
            foreach (AggregationBlock aggregationBlock in aggregationBlocks)
            {
                aggregationBlock.CreateMatchGraph(this.MatchClause);
                vertexAndEdgeAliases.Add(aggregationBlock.AggregationAlias);
                foreach (string alias in aggregationBlock.TableList)
                {
                    vertexAndEdgeAliases.Add(alias);
                }
            }
            vertexAndEdgeAliases.Remove("dummy");

            // Normalizes the search condition into conjunctive predicates
            BooleanExpressionNormalizeVisitor booleanNormalize = new BooleanExpressionNormalizeVisitor();
            List<WBooleanExpression> conjunctivePredicates =
                this.WhereClause != null && this.WhereClause.SearchCondition != null ?
                    booleanNormalize.Invoke(this.WhereClause.SearchCondition) :
                    new List<WBooleanExpression>();

            // A list of predicates and their accessed table references 
            // Predicates in this list are those that cannot be assigned to the match graph
            List<Tuple<WBooleanExpression, HashSet<string>>>
                predicatesAccessedTableReferences = new List<Tuple<WBooleanExpression, HashSet<string>>>();
            AccessedTableColumnVisitor columnVisitor = new AccessedTableColumnVisitor();
            GraphviewRuntimeFunctionCountVisitor runtimeFunctionCountVisitor = new GraphviewRuntimeFunctionCountVisitor();

            // Try to attach predicate to MatchNodes and MatchEdges
            foreach (WBooleanExpression predicate in conjunctivePredicates)
            {
                bool isOnlyTargetTableReferenced;
                bool useGraphViewRuntimeFunction = runtimeFunctionCountVisitor.Invoke(predicate) > 0;
                Dictionary<string, HashSet<string>> tableColumnReferences = columnVisitor.Invoke(predicate,
                    vertexAndEdgeAliases, out isOnlyTargetTableReferenced);

                // If failed, these MatchNodes and MatchEdges need to provide referencing properties, used for filterOperators
                if (useGraphViewRuntimeFunction
                    || !isOnlyTargetTableReferenced
                    || !this.TryAttachPredicate(aggregationBlocks, predicate, tableColumnReferences))
                {
                    this.AttachProperties(aggregationBlocks, tableColumnReferences);
                    predicatesAccessedTableReferences.Add(
                        new Tuple<WBooleanExpression, HashSet<string>>(predicate,
                            new HashSet<string>(tableColumnReferences.Keys)));
                }
            }

            // Attach referencing properties for later runtime evaluation
            foreach (WSelectElement selectElement in SelectElements)
            {
                bool isOnlyTargetTableReferenced;
                Dictionary<string, HashSet<string>> tableColumnReferences = columnVisitor.Invoke(selectElement,
                    vertexAndEdgeAliases, out isOnlyTargetTableReferenced);
                this.AttachProperties(aggregationBlocks, tableColumnReferences);
            }

            // Generate optimal traversal orders according to AggregateionBlocks
            foreach (AggregationBlock aggregationBlock in aggregationBlocks)
            {
                // Find input dependency and attach it to AggregationBlock
                Dictionary<string, HashSet<string>> tableInputDependency = new Dictionary<string, HashSet<string>>();
                foreach (KeyValuePair<string, NonMatchTable> pair in aggregationBlock.NonMatchTables)
                {
                    bool isOnlyTargetTableReferenced;
                    Dictionary<string, HashSet<string>> tableColumnReferences = columnVisitor.Invoke(
                        pair.Value.TableReference, vertexAndEdgeAliases, out isOnlyTargetTableReferenced);
                    this.AttachProperties(aggregationBlocks, tableColumnReferences);
                    tableInputDependency[pair.Key] = new HashSet<string>();
                    foreach (string alias in tableColumnReferences.Keys.ToList())
                    {
                        tableInputDependency[pair.Key].Add(alias);
                    }
                }

                // Find the optimal traversal order
                TraversalOrder blockTraversalOrder = ConstructExecutionOrder(aggregationBlock, tableInputDependency, predicatesAccessedTableReferences);
                // Append traversal order
                traversalOrder.Append(blockTraversalOrder);
            }

            // Construct Optimal operator chain according TraversalOrder
            List<GraphViewExecutionOperator> operatorChain = ConstructOperatorChain(context, command, traversalOrder);

            // Construct Project Operator or ProjectAggregation
            ConstructProjectOperator(command, context, SelectElements.Select(e => e as WSelectScalarExpression).ToList(), operatorChain);

            // Construct Operators according AggregationBlocks and remaining predicates
            return operatorChain.Last();
        }

        private bool TryAttachPredicate(List<AggregationBlock> aggregationBlocks, WBooleanExpression predicate,
            Dictionary<string, HashSet<string>> tableColumnReferences)
        {
            if (tableColumnReferences.Count > 1)
                return false;

            bool attachFlag = false;

            foreach (AggregationBlock aggregationBlock in aggregationBlocks)
            {
                attachFlag |= aggregationBlock.GraphPattern.TryAttachPredicate(predicate, tableColumnReferences);
                if (attachFlag)
                {
                    break;
                }
            }

            return attachFlag;
        }

        private void AttachProperties(List<AggregationBlock> aggregationBlocks, Dictionary<string, HashSet<string>> tableColumnReferences)
        {
            foreach (AggregationBlock aggregationBlock in aggregationBlocks)
            {
                aggregationBlock.GraphPattern?.AttachProperties(tableColumnReferences);
            }
        }
        
        internal static bool CanBePushedToServer(GraphViewCommand command, MatchEdge matchEdge)
        {
            // For Compatible & Hybrid, we can't push edge predicates to server side
            if (command.Connection.GraphType != GraphType.GraphAPIOnly) {
                Debug.Assert(command.Connection.EdgeSpillThreshold == 1);
                return false;
            }

            if (IsTraversalThroughPhysicalReverseEdge(matchEdge) && !command.Connection.UseReverseEdges) {
                return false;
            }

            return matchEdge != null && matchEdge.EdgeType != WEdgeType.BothEdge;
        }

        internal static MatchEdge GetPushedToServerEdge(GraphViewCommand command,
            Tuple<MatchNode, MatchEdge, List<MatchEdge>, List<MatchEdge>, List<MatchEdge>> tuple)
        {
            MatchNode currentNode = tuple.Item1;
            MatchEdge traversalEdge = tuple.Item3.Count > 0 ? tuple.Item3[0] : null;
            bool hasNoBackwardingOrForwardingEdges = tuple.Item4.Count == 0 && tuple.Item5.Count == 0;

            MatchEdge pushedToServerEdge = null;
            if (hasNoBackwardingOrForwardingEdges)
            {
                if (traversalEdge != null)
                {
                    pushedToServerEdge = CanBePushedToServer(command, traversalEdge)
                        ? traversalEdge
                        : null;
                }
                else if (currentNode.DanglingEdges.Count == 1)
                {
                    pushedToServerEdge = CanBePushedToServer(command, currentNode.DanglingEdges[0])
                        ? currentNode.DanglingEdges[0]
                        : null;
                }
            }

            return pushedToServerEdge;
        }

        internal static void ConstructJsonQueryOnNode(GraphViewCommand command, MatchNode node, MatchEdge edge, string partitionKey)
        {
            string nodeAlias = node.NodeAlias;
            string edgeAlias = null;
            List<string> nodeProperties = new List<string> { nodeAlias };
            List<string> edgeProperties = new List<string>();
            bool isReverseAdj = edge != null && IsTraversalThroughPhysicalReverseEdge(edge);
            bool isStartVertexTheOriginVertex = edge != null && !edge.IsReversed;


            var jsonQuery = new JsonQuery
            {
                NodeAlias = nodeAlias
            };
            //
            // SELECT N_0 FROM Node N_0
            //
            jsonQuery.AddSelectElement(nodeAlias);

            jsonQuery.FlatProperties.Add(partitionKey);

            nodeProperties.AddRange(node.Properties);

            if (edge != null)
            {
                edgeAlias = edge.LinkAlias;
                edgeProperties.Add(edge.LinkAlias);
                edgeProperties.Add(isReverseAdj.ToString());
                edgeProperties.Add(isStartVertexTheOriginVertex.ToString());

                //
                // SELECT N_0, E_0 FROM Node N_0 ...
                //
                jsonQuery.EdgeAlias = edgeAlias;
                jsonQuery.AddSelectElement(edgeAlias);

                edgeProperties.AddRange(edge.Properties);
            }

            //
            // Now we don't try to use a JOIN clause to fetch the edges along with the vertex unless in GraphAPI only graph
            // Thus, `edgeCondition` is always null
            //
            if (command.Connection.GraphType != GraphType.GraphAPIOnly)
            {
                Debug.Assert(edge == null);
            }

            WBooleanExpression edgeCondition = null;
            if (edge != null)
            {
                // pairs in this dict will be used in JOIN clause
                jsonQuery.JoinDictionary.Add(edgeAlias, $"{nodeAlias}.{(isReverseAdj ? DocumentDBKeywords.KW_VERTEX_REV_EDGE : DocumentDBKeywords.KW_VERTEX_EDGE)}");


                foreach (WBooleanExpression predicate in edge.Predicates)
                {
                    edgeCondition = WBooleanBinaryExpression.Conjunction(edgeCondition, predicate);
                }
            }

            if (edgeCondition != null)
            {
                edgeCondition = new WBooleanBinaryExpression
                {
                    BooleanExpressionType = BooleanBinaryExpressionType.Or,
                    FirstExpr = new WBooleanParenthesisExpression
                    {
                        Expression = edgeCondition
                    },
                    SecondExpr = new WBooleanComparisonExpression
                    {
                        ComparisonType = BooleanComparisonType.Equals,
                        FirstExpr = new WColumnReferenceExpression(nodeAlias, isReverseAdj
                            ? DocumentDBKeywords.KW_VERTEX_REVEDGE_SPILLED
                            : DocumentDBKeywords.KW_VERTEX_EDGE_SPILLED),
                        SecondExpr = new WValueExpression("true")
                    }
                };
                jsonQuery.FlatProperties.Add(isReverseAdj ? DocumentDBKeywords.KW_VERTEX_REVEDGE_SPILLED : DocumentDBKeywords.KW_VERTEX_EDGE_SPILLED);
            }

            // Most important variable of a JsonQuery object
            jsonQuery.RawWhereClause = new WBooleanComparisonExpression
            {
                ComparisonType = BooleanComparisonType.Equals,
                FirstExpr = new WColumnReferenceExpression(nodeAlias, DocumentDBKeywords.KW_EDGEDOC_IDENTIFIER),
                SecondExpr = new WValueExpression("null")
            };
            // Note: this move below protects that column name from replacing.(DocDB ToString)
            jsonQuery.FlatProperties.Add(DocumentDBKeywords.KW_EDGEDOC_IDENTIFIER);

            WBooleanExpression nodeCondition = null;
            foreach (WBooleanExpression predicate in node.Predicates)
            {
                nodeCondition = WBooleanBinaryExpression.Conjunction(nodeCondition, predicate);
            }

            if (nodeCondition != null)
            {
                jsonQuery.WhereConjunction(nodeCondition, BooleanBinaryExpressionType.And);
            }

            if (edgeCondition != null)
            {
                jsonQuery.WhereConjunction(edgeCondition, BooleanBinaryExpressionType.And);
            }

            jsonQuery.NodeProperties = nodeProperties;
            jsonQuery.EdgeProperties = edgeProperties;

            node.AttachedJsonQuery = jsonQuery;
        }

        internal static void ConstructJsonQueryOnEdge(GraphViewCommand command, MatchNode node, MatchEdge edge)
        {
            string nodeAlias = node.NodeAlias;
            string edgeAlias = edge.LinkAlias;
            List<string> nodeProperties = new List<string> { nodeAlias };
            List<string> edgeProperties = new List<string> { edgeAlias };
            nodeProperties.AddRange(node.Properties);
            edgeProperties.AddRange(edge.Properties);

            var jsonQuery = new JsonQuery
            {
                NodeAlias = nodeAlias,
                EdgeAlias = edgeAlias
            };

            //
            // SELECT N_0, E_0 FROM Node N_0 Join E_0 IN N_0._edge
            //
            jsonQuery.AddSelectElement(nodeAlias);
            jsonQuery.AddSelectElement(edgeAlias);

            jsonQuery.JoinDictionary.Add(edgeAlias, $"{nodeAlias}.{DocumentDBKeywords.KW_VERTEX_EDGE}");

            WBooleanExpression tempEdgeCondition = null;
            foreach (WBooleanExpression predicate in edge.Predicates)
            {
                tempEdgeCondition = WBooleanBinaryExpression.Conjunction(tempEdgeCondition, predicate);
            }

            // Where condition constructing
            //
            // WHERE ((N_0._isEdgeDoc = true AND N_0._is_reverse = false) OR N_0._edgeSpilled = false)
            // AND (edgeConditionString)
            //
            jsonQuery.RawWhereClause = new WBooleanComparisonExpression
            {
                ComparisonType = BooleanComparisonType.Equals,
                FirstExpr = new WColumnReferenceExpression(nodeAlias, DocumentDBKeywords.KW_EDGEDOC_IDENTIFIER),
                SecondExpr = new WValueExpression("true", false)
            };
            // Note: this move below protects that column name from replacing.(DocDB ToString)
            jsonQuery.FlatProperties.Add(DocumentDBKeywords.KW_EDGEDOC_IDENTIFIER);

            jsonQuery.WhereConjunction(new WBooleanComparisonExpression
            {
                ComparisonType = BooleanComparisonType.Equals,
                FirstExpr = new WColumnReferenceExpression(nodeAlias, DocumentDBKeywords.KW_EDGEDOC_ISREVERSE),
                SecondExpr = new WValueExpression("false", false)
            }, BooleanBinaryExpressionType.And);
            jsonQuery.FlatProperties.Add(DocumentDBKeywords.KW_EDGEDOC_ISREVERSE);

            jsonQuery.WhereConjunction(new WBooleanComparisonExpression
            {
                ComparisonType = BooleanComparisonType.Equals,
                FirstExpr = new WColumnReferenceExpression(nodeAlias, DocumentDBKeywords.KW_VERTEX_EDGE_SPILLED),
                SecondExpr = new WValueExpression("false", false)
            }, BooleanBinaryExpressionType.Or);
            jsonQuery.FlatProperties.Add(DocumentDBKeywords.KW_VERTEX_EDGE_SPILLED);

            if (tempEdgeCondition != null)
            {
                jsonQuery.WhereConjunction(tempEdgeCondition, BooleanBinaryExpressionType.And);
            }

            jsonQuery.NodeProperties = nodeProperties;
            jsonQuery.EdgeProperties = edgeProperties;

            edge.AttachedJsonQuery = jsonQuery;
        }

        //internal static OperatorChain ConstructExecutionOperators(GraphViewCommand command,
        //    QueryCompilationContext context, AggregationBlock aggregationBlock,
        //    List<Tuple<WBooleanExpression, HashSet<string>>> predicatesAccessedTableReferences,
        //    List<GraphViewExecutionOperator> operatorChain)
        //{
        //    BlockOptimizer blockOptimizer = new BlockOptimizer(aggregationBlock, predicatesAccessedTableReferences);
        //    return blockOptimizer.GenerateOptimalTraversalOrder(command, context, operatorChain);
        //}

        internal static TraversalOrder ConstructExecutionOrder(
            AggregationBlock aggregationBlock,
            Dictionary<string, HashSet<string>> tableInputDependency, 
            List<Tuple<WBooleanExpression, HashSet<string>>> predicatesAccessedTableReferences)
        {
            BlockOptimizer blockOptimizer = new BlockOptimizer(aggregationBlock);
            return blockOptimizer.GenerateOptimalTraversalOrder(tableInputDependency, predicatesAccessedTableReferences);
        }

        internal static List<GraphViewExecutionOperator> ConstructOperatorChain(QueryCompilationContext context, 
            GraphViewCommand command,
            TraversalOrder traversalOrder)
        {
            List<GraphViewExecutionOperator> operatorChain = new List<GraphViewExecutionOperator>();

            // TODO: finish this method

            return operatorChain;
        }

        internal static void ConstructProjectOperator(GraphViewCommand command, QueryCompilationContext context,
            List<WSelectScalarExpression> selectScalarExprList, List<GraphViewExecutionOperator> operatorChain)
        {
            int aggregateCount = 0;

            foreach (WSelectScalarExpression selectScalar in selectScalarExprList)
            {
                if (selectScalar.SelectExpr is WFunctionCall)
                {
                    WFunctionCall fcall = selectScalar.SelectExpr as WFunctionCall;
                    switch (fcall.FunctionName.Value.ToUpper())
                    {
                        case "COUNT":
                        case "FOLD":
                        case "TREE":
                        case "CAP":
                        case "SUM":
                        case "MAX":
                        case "MIN":
                        case "MEAN":
                            aggregateCount++;
                            break;
                        default:
                            break;
                    }
                }
            }

            if (aggregateCount == 0)
            {
                // If operatorChain is empty and OuterContextOp is null, this is a SelectQueryBlock only selects WValueExpression
                // and a ConstantSource is faked as the input
                var projectOperator =
                    new ProjectOperator(operatorChain.Any()
                        ? operatorChain.Last()
                        : (context.OuterContextOp ?? new EnumeratorOperator()));


                // When CarryOn is set, in addition to the SELECT elements in the SELECT clause,
                // the query also projects fields from its parent context.
                if (context.CarryOn)
                {
                    foreach (var fieldPair in context.ParentContextRawRecordLayout.OrderBy(e => e.Value))
                    {
                        FieldValue fieldSelectFunc = new FieldValue(fieldPair.Value);
                        projectOperator.AddSelectScalarElement(fieldSelectFunc);
                    }
                }
                else if (context.InBatchMode)
                {
                    FieldValue indexValue = new FieldValue(0);
                    projectOperator.AddSelectScalarElement(indexValue);
                }


                foreach (var expr in selectScalarExprList)
                {
                    ScalarFunction scalarFunction = expr.SelectExpr.CompileToFunction(context, command);
                    projectOperator.AddSelectScalarElement(scalarFunction);
                }

                operatorChain.Add(projectOperator);
                context.CurrentExecutionOperator = projectOperator;
            }
            else
            {
                ProjectAggregation projectAggregationOp = context.InBatchMode ?
                    new ProjectAggregationInBatch(operatorChain.Any()
                        ? operatorChain.Last()
                        : context.OuterContextOp) :
                    new ProjectAggregation(operatorChain.Any()
                        ? operatorChain.Last()
                        : context.OuterContextOp);

                foreach (var selectScalar in selectScalarExprList)
                {
                    WFunctionCall fcall = selectScalar.SelectExpr as WFunctionCall;

                    if (fcall == null)
                    {
                        projectAggregationOp.AddAggregateSpec(null, null);
                    }

                    switch (fcall.FunctionName.Value.ToUpper())
                    {
                        case "COUNT":
                            projectAggregationOp.AddAggregateSpec(new CountFunction(), new List<ScalarFunction>());
                            break;
                        case "FOLD":
                            WFunctionCall foldedFunction = fcall.Parameters[0] as WFunctionCall;
                            if (foldedFunction == null)
                                throw new SyntaxErrorException("The parameter of a Fold function must be a Compose1 function.");
                            projectAggregationOp.AddAggregateSpec(new FoldFunction(),
                                new List<ScalarFunction> { foldedFunction.CompileToFunction(context, command), });
                            break;
                        case "TREE":
                            WColumnReferenceExpression pathField = fcall.Parameters[0] as WColumnReferenceExpression;
                            projectAggregationOp.AddAggregateSpec(
                                new TreeFunction(new TreeState(GremlinKeyword.func.Tree)),
                                new List<ScalarFunction>() { pathField.CompileToFunction(context, command) });
                            break;
                        case "CAP":
                            CapFunction capFunction = new CapFunction();
                            for (int i = 0; i < fcall.Parameters.Count; i += 2)
                            {
                                WColumnNameList columnNameList = fcall.Parameters[i] as WColumnNameList;
                                WValueExpression capName = fcall.Parameters[i + 1] as WValueExpression;

                                IAggregateFunction sideEffectFunction;
                                if (!context.SideEffectFunctions.TryGetValue(capName.Value, out sideEffectFunction))
                                {
                                    throw new GraphViewException("SideEffect state " + capName + " doesn't exist in the context");
                                }
                                capFunction.AddCapatureSideEffectState(capName.Value, sideEffectFunction);
                            }
                            projectAggregationOp.AddAggregateSpec(capFunction, new List<ScalarFunction>());
                            break;
                        case "SUM":
                            WColumnReferenceExpression targetField = fcall.Parameters[0] as WColumnReferenceExpression;
                            projectAggregationOp.AddAggregateSpec(
                                new SumFunction(),
                                new List<ScalarFunction> { targetField.CompileToFunction(context, command) });
                            break;
                        case "MAX":
                            targetField = fcall.Parameters[0] as WColumnReferenceExpression;
                            projectAggregationOp.AddAggregateSpec(
                                new MaxFunction(),
                                new List<ScalarFunction> { targetField.CompileToFunction(context, command) });
                            break;
                        case "MIN":
                            targetField = fcall.Parameters[0] as WColumnReferenceExpression;
                            projectAggregationOp.AddAggregateSpec(
                                new MinFunction(),
                                new List<ScalarFunction> { targetField.CompileToFunction(context, command) });
                            break;
                        case "MEAN":
                            targetField = fcall.Parameters[0] as WColumnReferenceExpression;
                            projectAggregationOp.AddAggregateSpec(
                                new MeanFunction(),
                                new List<ScalarFunction> { targetField.CompileToFunction(context, command) });
                            break;
                        default:
                            projectAggregationOp.AddAggregateSpec(null, null);
                            break;
                    }
                }

                operatorChain.Add(projectAggregationOp);
                context.CurrentExecutionOperator = projectAggregationOp;
            }
        }

        /// <summary>
        /// If using node._reverse_edge, return true.
        /// If using node._edge, return false
        /// </summary>
        /// <param name="edge"></param>
        /// <returns></returns>
        internal static bool IsTraversalThroughPhysicalReverseEdge(MatchEdge edge)
        {
            if ((edge.EdgeType == WEdgeType.OutEdge && edge.IsReversed)
                || edge.EdgeType == WEdgeType.InEdge && !edge.IsReversed)
                return true;
            return false;
        }
        
        //internal GraphViewExecutionOperator ConstructOperators(GraphViewCommand command, List<AggregationBlock> aggregationBlocks,
        //    QueryCompilationContext context, List<Tuple<WBooleanExpression, HashSet<string>>> predicatesAccessedTableReferences)
        //{
        //    // Construct Operators according to AggregationBlocks
        //    List<GraphViewExecutionOperator> chain = new List<GraphViewExecutionOperator>();

        //    foreach (AggregationBlock aggregationBlock in aggregationBlocks)
        //    {
        //        OperatorChain operatorChain = ConstructExecutionOperators(command, context, aggregationBlock,
        //            predicatesAccessedTableReferences, chain);
        //        context.Update(operatorChain.Context);
        //        chain = operatorChain.Chain;
        //        predicatesAccessedTableReferences = operatorChain.RemainingPredicatesAccessedTableReferences;
        //    }

        //    // Construct ProjectOperator according to SELECT Clause
        //    ConstructProjectOperator(command, context, SelectElements.Select(e => e as WSelectScalarExpression).ToList(), chain);

        //    return chain.Last();
        //}
    }

    partial class WWithPathClause
    {
        //internal override GraphViewExecutionOperator Generate(GraphViewConnection dbConnection)
        //{
        //    foreach (var path in Paths)
        //    {
        //        //path.Item2.SelectElements = new List<WSelectElement>();
        //        PathOperators.Add(new Tuple<string, GraphViewExecutionOperator, int>(path.Item1,
        //            path.Item2.Generate(dbConnection), path.Item3));
        //    }
        //    if (PathOperators.Count != 0) return PathOperators.First().Item2;
        //    else return null;
        //}
    }

    partial class WChoose
    {
        //internal override GraphViewExecutionOperator Generate(GraphViewConnection dbConnection)
        //{
        //    List<GraphViewExecutionOperator> Source = new List<GraphViewExecutionOperator>();
        //    foreach (var x in InputExpr)
        //    {
        //        Source.Add(x.Generate(dbConnection));
        //    }
        //    return new ConcatenateOperator(Source);
        //}
    }

    partial class WCoalesce
    {
        //internal override GraphViewExecutionOperator Generate(GraphViewConnection dbConnection)
        //{
        //    List<GraphViewExecutionOperator> Source = new List<GraphViewExecutionOperator>();
        //    foreach (var x in InputExpr)
        //    {
        //        Source.Add(x.Generate(dbConnection));
        //    }
        //    var op = new CoalesceOperator(Source, CoalesceNumber);
        //    return new OutputOperator(op, op.header, null);
        //}
    }

    partial class WSqlBatch
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            QueryCompilationContext priorContext = new QueryCompilationContext();
            GraphViewExecutionOperator op = null;
            foreach (WSqlStatement st in Statements)
            {
                QueryCompilationContext statementContext = new QueryCompilationContext(priorContext.TemporaryTableCollection,
                    priorContext.SideEffectFunctions, priorContext.SideEffectStates, priorContext.SelectOperators,
                    priorContext.OptimalSolutions, priorContext.Containers);
                op = st.Compile(statementContext, command);
                priorContext = statementContext;
            }

#if USE_SERIALIZE
            SerializationData.SetSideEffectStates(priorContext.SideEffectFunctions);
            SerializationData.SetContainers(priorContext.Containers);

            GraphViewSerializer.Serialize(command, op);
            GraphViewCommand newCommand;
            GraphViewExecutionOperator newOp = GraphViewSerializer.Deserialize(out newCommand);
            // because the command is used in test-case later, we can only set fields and properties of the command.
            command.SetCommand(newCommand);

            return newOp;
#endif

            // Returns the last execution operator
            // To consider: prior execution operators that have no links to the last operator will not be executed.
            return op;
        }
    }

    partial class WSetVariableStatement
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            if (_expression.GetType() != typeof(WScalarSubquery))
            {
                throw new NotImplementedException();
            }

            WSqlStatement subquery = (_expression as WScalarSubquery).SubQueryExpr;
            GraphViewExecutionOperator subqueryOp = subquery.Compile(context, command);
            TemporaryTableHeader tmpTableHeader = context.ToTableHeader();
            // Adds the table populated by the statement as a temporary table to the context
            context.TemporaryTableCollection[_variable.Name] = new Tuple<TemporaryTableHeader, GraphViewExecutionOperator>(tmpTableHeader, subqueryOp);

            return subqueryOp;
        }
    }

    partial class WUnionTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            Container container = new Container();
            int containerIndex = context.AddContainers(container);
            UnionOperator unionOp = new UnionOperator(context.CurrentExecutionOperator, container, containerIndex);
            bool isUnionWithoutAnyBranch = Parameters.Count == 0 || Parameters[0] is WValueExpression;

            WSelectQueryBlock firstSelectQuery = null;
            if (!isUnionWithoutAnyBranch)
            {
                foreach (WScalarExpression parameter in Parameters)
                {
                    WScalarSubquery scalarSubquery = parameter as WScalarSubquery;
                    if (scalarSubquery == null)
                    {
                        throw new SyntaxErrorException("The input of an union table reference must be one or more scalar subqueries.");
                    }

                    if (firstSelectQuery == null)
                    {
                        firstSelectQuery = scalarSubquery.SubQueryExpr as WSelectQueryBlock;
                        if (firstSelectQuery == null)
                        {
                            throw new SyntaxErrorException("The input of an union table reference must be one or more select query blocks.");
                        }
                    }

                    QueryCompilationContext subcontext = new QueryCompilationContext(context);
                    subcontext.OuterContextOp.SetContainer(container, containerIndex);
                    subcontext.InBatchMode = context.InBatchMode;
                    subcontext.CarryOn = true;
                    GraphViewExecutionOperator traversalOp = scalarSubquery.SubQueryExpr.Compile(subcontext, command);
                    unionOp.AddTraversal(traversalOp);
                }
            }

            // Updates the raw record layout. The columns of this table-valued function 
            // are specified by the select elements of the input subqueries.
            if (!isUnionWithoutAnyBranch)
            {
                foreach (WSelectElement selectElement in firstSelectQuery.SelectElements)
                {
                    WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                    if (selectScalar == null) {
                        throw new SyntaxErrorException("The input subquery of an union table reference can only select scalar elements.");
                    }
                    Debug.Assert(selectScalar.ColumnName != null, "selectScalar.ColumnName != null");

                    WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                    
                    context.AddField(Alias.Value, selectScalar.ColumnName, columnRef?.ColumnGraphType ?? ColumnGraphType.Value);
                }
            }
            else
            {
                context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);
                foreach (WScalarExpression parameter in Parameters)
                {
                    WValueExpression columnName = parameter as WValueExpression;
                    context.AddField(Alias.Value, columnName.Value, ColumnGraphType.Value);
                }
            }

            context.CurrentExecutionOperator = unionOp;
            return unionOp;
        }
    }

    partial class WCoalesceTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            ContainerWithFlag container = new ContainerWithFlag();
            // int containerIndex = SerializationData.AddContainers(container);
            int containerIndex = context.AddContainers(container);
            CoalesceOperator coalesceOp = new CoalesceOperator(context.CurrentExecutionOperator, container, containerIndex);
               
            WSelectQueryBlock firstSelectQuery = null;
            foreach (WScalarExpression parameter in Parameters)
            {
                WScalarSubquery scalarSubquery = parameter as WScalarSubquery;
                if (scalarSubquery == null)
                {
                    throw new SyntaxErrorException("The input of a coalesce table reference must be one or more scalar subqueries.");
                }

                if (firstSelectQuery == null)
                {
                    firstSelectQuery = scalarSubquery.SubQueryExpr as WSelectQueryBlock;
                    if (firstSelectQuery == null)
                    {
                        throw new SyntaxErrorException("The input of a coalesce table reference must be one or more select query blocks.");
                    }
                }

                // Set all sub-traversals' source to a same `sourceEnumerator`, and turn on InBatchMode
                QueryCompilationContext subcontext = new QueryCompilationContext(context);
                subcontext.OuterContextOp.SetContainer(container, containerIndex);
                subcontext.AddField(GremlinKeyword.IndexTableName, command.IndexColumnName, ColumnGraphType.Value, true);
                subcontext.InBatchMode = true;
                GraphViewExecutionOperator traversalOp = scalarSubquery.SubQueryExpr.Compile(subcontext, command);
                coalesceOp.AddTraversal(traversalOp);
            }

            // Updates the raw record layout. The columns of this table-valued function 
            // are specified by the select elements of the input subqueries.
            foreach (WSelectElement selectElement in firstSelectQuery.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                if (selectScalar == null)
                {
                    throw new SyntaxErrorException("The input subquery of a coalesce table reference can only select scalar elements.");
                }
                Debug.Assert(selectScalar.ColumnName != null, "selectScalar.ColumnName != null");

                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                
                context.AddField(Alias.Value, selectScalar.ColumnName, columnRef?.ColumnGraphType ?? ColumnGraphType.Value);
            }

            context.CurrentExecutionOperator = coalesceOp;
            return coalesceOp;
        }
    }

    partial class WOptionalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WSelectQueryBlock contextSelect, optionalSelect;
            this.Split(out contextSelect, out optionalSelect);

            List<int> inputIndexes = new List<int>();
            List<Tuple<WColumnReferenceExpression, string>> columnList =
                new List<Tuple<WColumnReferenceExpression, string>>();

            foreach (WSelectElement selectElement in contextSelect.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                if (selectScalar == null)
                {
                    throw new SyntaxErrorException(
                        "The SELECT elements of the sub-queries in an optional table reference must be select scalar elements.");
                }

                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;

                if (columnRef != null)
                {
                    int index;
                    if (!context.TryLocateColumnReference(columnRef, out index))
                    {
                        throw new SyntaxErrorException("Syntax Error!!!");
                    }

                    inputIndexes.Add(index);

                    columnList.Add(
                        new Tuple<WColumnReferenceExpression, string>(
                            new WColumnReferenceExpression(
                                this.Alias.Value,
                                selectScalar.ColumnName ?? columnRef.ColumnName,
                                columnRef.ColumnGraphType),
                            selectScalar.ColumnName));
                }
                else
                {
                    WValueExpression nullExpression = selectScalar.SelectExpr as WValueExpression;
                    if (nullExpression == null)
                    {
                        throw new SyntaxErrorException(
                            "The SELECT elements of the sub-queries in a optional table reference must be column references or WValueExpression.");
                    }

                    if (nullExpression.ToString().Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        inputIndexes.Add(-1);
                    }

                    columnList.Add(
                        new Tuple<WColumnReferenceExpression, string>(
                            new WColumnReferenceExpression(
                                this.Alias.Value,
                                selectScalar.ColumnName,
                                ColumnGraphType.Value),
                            selectScalar.ColumnName));
                }
            }

            QueryCompilationContext targetSubContext = new QueryCompilationContext(context);
            Container targetContainer = new Container();
            int targetContainerIndex = context.AddContainers(targetContainer);
            targetSubContext.OuterContextOp.SetContainer(targetContainer, targetContainerIndex);
            targetSubContext.AddField(GremlinKeyword.IndexTableName, command.IndexColumnName, ColumnGraphType.Value, true);
            targetSubContext.InBatchMode = true;
            GraphViewExecutionOperator targetSubqueryOp = optionalSelect.Compile(targetSubContext, command);

            QueryCompilationContext subcontext = new QueryCompilationContext(context);
            Container optinalContainer = new Container();
            int optinalContainerIndex = context.AddContainers(optinalContainer);
            subcontext.OuterContextOp.SetContainer(optinalContainer, optinalContainerIndex);
            subcontext.CarryOn = true;
            subcontext.InBatchMode = context.InBatchMode;
            GraphViewExecutionOperator optionalTraversalOp = optionalSelect.Compile(subcontext, command);

            OptionalOperator optionalOp = new OptionalOperator(
                context.CurrentExecutionOperator,
                inputIndexes,
                targetContainer,
                targetContainerIndex,
                targetSubqueryOp,
                optinalContainer,
                optinalContainerIndex,
                optionalTraversalOp);

            context.CurrentExecutionOperator = optionalOp;

            // Updates the raw record layout. The columns of this table-valued function 
            // are specified by the select elements of the input subqueries.
            foreach (Tuple<WColumnReferenceExpression, string> tuple in columnList)
            {
                WColumnReferenceExpression columnRef = tuple.Item1;
                string selectElementAlias = tuple.Item2;
                context.AddField(this.Alias.Value, selectElementAlias ?? columnRef.ColumnName, columnRef.ColumnGraphType);
            }

            return optionalOp;
        }

    }

    partial class WLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WScalarSubquery localSubquery = Parameters[0] as WScalarSubquery;
            if (localSubquery == null)
            {
                throw new SyntaxErrorException("The input of a local table reference must be a scalar subquery.");
            }
            WSelectQueryBlock localSelect = localSubquery.SubQueryExpr as WSelectQueryBlock;
            if (localSelect == null)
            {
                throw new SyntaxErrorException("The sub-query must be a select query block.");
            }

            QueryCompilationContext subcontext = new QueryCompilationContext(context);
            Container container = new Container();
            int containerIndex = context.AddContainers(container);
            subcontext.OuterContextOp.SetContainer(container, containerIndex);
            subcontext.AddField(GremlinKeyword.IndexTableName, command.IndexColumnName, ColumnGraphType.Value, true);
            subcontext.InBatchMode = true;
            GraphViewExecutionOperator localTraversalOp = localSelect.Compile(subcontext, command);
            LocalOperator localOp = new LocalOperator(context.CurrentExecutionOperator, localTraversalOp, container, containerIndex);

            foreach (WSelectElement selectElement in localSelect.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                if (selectScalar == null) {
                    throw new SyntaxErrorException("The SELECT elements of the sub-query in a local table reference must be select scalar elements.");
                }
                Debug.Assert(selectScalar.ColumnName != null, "selectScalar.ColumnName != null");

                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                
                context.AddField(Alias.Value, selectScalar.ColumnName, columnRef?.ColumnGraphType ?? ColumnGraphType.Value);
            }

            return localOp;
        }
    }

    partial class WFlatMapTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WScalarSubquery flatMapSubquery = Parameters[0] as WScalarSubquery;
            if (flatMapSubquery == null)
            {
                throw new SyntaxErrorException("The input of a flatMap table reference must be a scalar subquery.");
            }
            WSelectQueryBlock flatMapSelect = flatMapSubquery.SubQueryExpr as WSelectQueryBlock;
            if (flatMapSelect == null)
            {
                throw new SyntaxErrorException("The sub-query must be a select query block.");
            }

            Container container = new Container();
            int containerIndex = context.AddContainers(container);
            QueryCompilationContext subcontext = new QueryCompilationContext(context);
            subcontext.OuterContextOp.SetContainer(container, containerIndex);
            subcontext.AddField(GremlinKeyword.IndexTableName, command.IndexColumnName, ColumnGraphType.Value, true);
            subcontext.InBatchMode = true;
            GraphViewExecutionOperator flatMapTraversalOp = flatMapSelect.Compile(subcontext, command);

            FlatMapOperator flatMapOp = new FlatMapOperator(context.CurrentExecutionOperator, flatMapTraversalOp, container, containerIndex);
            context.CurrentExecutionOperator = flatMapOp;

            foreach (WSelectElement selectElement in flatMapSelect.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                if (selectScalar == null)
                {
                    throw new SyntaxErrorException("The SELECT elements of the sub-query in a flatMap table reference must be select scalar elements.");
                }
                Debug.Assert(selectScalar.ColumnName != null, "selectScalar.ColumnName != null");

                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                
                context.AddField(Alias.Value, selectScalar.ColumnName, columnRef?.ColumnGraphType ?? ColumnGraphType.Value);
            }

            return flatMapOp;
        }
    }

    partial class WBoundNodeTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            Debug.Assert(context.CurrentExecutionOperator != null, "context.CurrentExecutionOperator != null");

            string nodeAlias = Alias.Value;
            MatchNode matchNode = new MatchNode
            {
                AttachedJsonQuery = null,
                NodeAlias = nodeAlias,
                Predicates = new List<WBooleanExpression>(),
                Properties = new HashSet<string>(),
            };

            foreach (WScalarExpression expression in this.Parameters)
            {
                WValueExpression populateProperty = expression as WValueExpression;
                Debug.Assert(populateProperty != null, "populateProperty != null");

                matchNode.Properties.Add(populateProperty.Value);
            }

            WSelectQueryBlock.ConstructJsonQueryOnNode(command, matchNode, null, command.Connection.RealPartitionKey);
            //WSelectQueryBlock.ConstructJsonQueryOnNodeViaExternalAPI(matchNode, null);

            FetchNodeOperator fetchNodeOp = new FetchNodeOperator(
                command, 
                matchNode.AttachedJsonQuery
                /*matchNode.AttachedJsonQueryOfNodesViaExternalAPI*/);

            foreach (string propertyName in matchNode.Properties) {
                ColumnGraphType columnGraphType = GraphViewReservedProperties.IsNodeReservedProperty(propertyName)
                    ? GraphViewReservedProperties.ReservedNodePropertiesColumnGraphTypes[propertyName]
                    : ColumnGraphType.Value;
                context.AddField(nodeAlias, propertyName, columnGraphType);
            }

            return new CartesianProductOperator(context.CurrentExecutionOperator, fetchNodeOp);
        }
    }

    partial class WEdgeToVertexTableReference
    {
        private const int edgeFieldParameteIndex = 0;
        private const int populatePropertyParameterStartIndex = 1;

        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression edgeFieldParameter = this.Parameters[edgeFieldParameteIndex] as WColumnReferenceExpression;
            Debug.Assert(edgeFieldParameter != null, "edgeFieldParameter != null");
            int edgeFieldIndex = context.LocateColumnReference(edgeFieldParameter);

            string nodeAlias = this.Alias.Value;

            MatchNode matchNode = new MatchNode {
                AttachedJsonQuery = null,
                NodeAlias = nodeAlias,
                Predicates = new List<WBooleanExpression>(),
                Properties = new HashSet<string>() { GremlinKeyword.Star },
            };

            for (int i = populatePropertyParameterStartIndex; i < this.Parameters.Count; i++) {
                WValueExpression populateProperty = this.Parameters[i] as WValueExpression;
                Debug.Assert(populateProperty != null, "populateProperty != null");

                matchNode.Properties.Add(populateProperty.Value);
            }

            bool isSendQueryRequired = !(matchNode.Properties.Count == 1 &&
                                         matchNode.Properties.First().Equals(DocumentDBKeywords.KW_DOC_ID));

            //
            // Construct JSON query
            //
            if (isSendQueryRequired) {
                WSelectQueryBlock.ConstructJsonQueryOnNode(command, matchNode, null, command.Connection.RealPartitionKey);
                //WSelectQueryBlock.ConstructJsonQueryOnNodeViaExternalAPI(matchNode, null);
            }

            TraversalOperator traversalOp = new TraversalOperator(
                context.CurrentExecutionOperator, command, 
                edgeFieldIndex, this.GetTraversalTypeParameter(),
                matchNode.AttachedJsonQuery/*, matchNode.AttachedJsonQueryOfNodesViaExternalAPI*/, null);
            context.CurrentExecutionOperator = traversalOp;

            // Update context's record layout
            if (isSendQueryRequired) {
                foreach (string propertyName in matchNode.Properties) {
                    ColumnGraphType columnGraphType = GraphViewReservedProperties.IsNodeReservedProperty(propertyName)
                        ? GraphViewReservedProperties.ReservedNodePropertiesColumnGraphTypes[propertyName]
                        : ColumnGraphType.Value;
                    context.AddField(nodeAlias, propertyName, columnGraphType);
                }
            }
            else {
                context.AddField(nodeAlias, GremlinKeyword.NodeID, ColumnGraphType.VertexId);
            }

            return traversalOp;
        }
    }

    partial class WEdgeToSinkVertexTableReference
    {
        internal override TraversalOperator.TraversalTypeEnum GetTraversalTypeParameter()
        {
            return TraversalOperator.TraversalTypeEnum.Sink;
        }
    }

    partial class WEdgeToSourceVertexTableReference
    {
        internal override TraversalOperator.TraversalTypeEnum GetTraversalTypeParameter()
        {
            return TraversalOperator.TraversalTypeEnum.Source;
        }
    }

    partial class WEdgeToOtherVertexTableReference
    {
        internal override TraversalOperator.TraversalTypeEnum GetTraversalTypeParameter()
        {
            return TraversalOperator.TraversalTypeEnum.Other;
        }
    }

    partial class WEdgeToBothVertexTableReference
    {
        internal override TraversalOperator.TraversalTypeEnum GetTraversalTypeParameter()
        {
            return TraversalOperator.TraversalTypeEnum.Both;
        }
    }

    partial class WVertexToEdgeTableReference
    {
        private const int startVertexParameterIndex = 0;
        private const int populatePropertyParameterStartIndex = 1;

        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context,
            GraphViewCommand command)
        {
            WColumnReferenceExpression startVertexParameter = this.Parameters[startVertexParameterIndex] as WColumnReferenceExpression;
            Debug.Assert(startVertexParameter != null, "startVertexParameter != null");
            int startVertexIndex = context.LocateColumnReference(startVertexParameter);

            string edgeAlias = this.Alias.Value;
            List<string> projectFields = new List<string>(GraphViewReservedProperties.ReservedEdgeProperties);

            for (int i = populatePropertyParameterStartIndex; i < this.Parameters.Count; i++) {
                WValueExpression propertyParameter = this.Parameters[i] as WValueExpression;
                Debug.Assert(propertyParameter != null, "propertyParameter != null");
                string field = propertyParameter.Value;
                if (!projectFields.Contains(field))
                    projectFields.Add(field);
            }

            Tuple<bool, bool> crossApplyTypeParameter = this.GetAdjListDecoderCrossApplyTypeParameter();
            bool crossApplyForwardAdj = crossApplyTypeParameter.Item1;
            bool crossApplyBackwardAdj = crossApplyTypeParameter.Item2;

            AdjacencyListDecoder adjListDecoder = new AdjacencyListDecoder(
                context.CurrentExecutionOperator, startVertexIndex,
                crossApplyForwardAdj, crossApplyBackwardAdj, 
                true, null, 
                projectFields, command, 
                context.RawRecordLayout.Count + projectFields.Count);

            context.CurrentExecutionOperator = adjListDecoder;

            // Update context's record layout
            context.AddField(edgeAlias, GremlinKeyword.EdgeSourceV, ColumnGraphType.EdgeSource);
            context.AddField(edgeAlias, GremlinKeyword.EdgeSinkV, ColumnGraphType.EdgeSink);
            context.AddField(edgeAlias, GremlinKeyword.EdgeOtherV, ColumnGraphType.Value);
            context.AddField(edgeAlias, GremlinKeyword.EdgeID, ColumnGraphType.EdgeId);
            context.AddField(edgeAlias, GremlinKeyword.Star, ColumnGraphType.EdgeObject);

            for (int i = GraphViewReservedProperties.ReservedEdgeProperties.Count; i < projectFields.Count; i++) {
                context.AddField(edgeAlias, projectFields[i], ColumnGraphType.Value);
            }

            return adjListDecoder;
        }
    }

    partial class WVertexToForwardEdgeTableReference
    {
        internal override Tuple<bool, bool> GetAdjListDecoderCrossApplyTypeParameter()
        {
            return new Tuple<bool, bool>(true, false);
        }
    }

    partial class WVertexToBackwordEdgeTableReference
    {
        internal override Tuple<bool, bool> GetAdjListDecoderCrossApplyTypeParameter()
        {
            return new Tuple<bool, bool>(false, true);
        }
    }

    partial class WVertexToBothEdgeTableReference
    {
        internal override Tuple<bool, bool> GetAdjListDecoderCrossApplyTypeParameter()
        {
            return new Tuple<bool, bool>(true, true);
        }
    }

    partial class WValuesTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            List<int> propertiesIndex = new List<int>();

            foreach (WScalarExpression expression in Parameters)
            {
                WColumnReferenceExpression targetParameter = expression as WColumnReferenceExpression;
                if (targetParameter != null)
                {
                    propertiesIndex.Add(context.LocateColumnReference(targetParameter));
                    continue;
                }

                throw new QueryCompilationException(
                    "Parameters of Properties table can only be WColumnReferenceExpression.");
            }

            GraphViewExecutionOperator valuesOp = new ValuesOperator(context.CurrentExecutionOperator, propertiesIndex);
            context.CurrentExecutionOperator = valuesOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return valuesOp;
        }
    }


    partial class WLabelTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            Debug.Assert(this.Parameters.Count == 1);

            WColumnReferenceExpression targetVertexOrEdge = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(targetVertexOrEdge != null);

            int targetVertexOrEdgeIndex = context.LocateColumnReference(targetVertexOrEdge);

            GraphViewExecutionOperator labelOp = new LabelOperator(context.CurrentExecutionOperator, targetVertexOrEdgeIndex);
            context.CurrentExecutionOperator = labelOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return labelOp;
        }
    }


    partial class WIdTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            Debug.Assert(this.Parameters.Count == 1);

            // Can be VertexField, EdgeField, or VertexSinglePropertyField
            WColumnReferenceExpression target = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(target != null);

            int targetIndex = context.LocateColumnReference(target);

            GraphViewExecutionOperator IdOp = new IdOperator(context.CurrentExecutionOperator, targetIndex);
            context.CurrentExecutionOperator = IdOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return IdOp;
        }
    }


    partial class WAllPropertiesTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression inputParameter = Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(inputParameter != null, "inputParameter != null");

            int inputTargetIndex = context.LocateColumnReference(inputParameter);

            List<string> populatedMetaproperties = new List<string>();
            for (int metaPropertiesIndex = 1; metaPropertiesIndex < Parameters.Count; metaPropertiesIndex++)
            {
                WValueExpression metaPropertyExpression = Parameters[metaPropertiesIndex] as WValueExpression;
                Debug.Assert(metaPropertyExpression != null, "metaPropertyExpression != null");
                
                populatedMetaproperties.Add(metaPropertyExpression.Value);
            }

            
            AllPropertiesOperator allPropertiesOp = new AllPropertiesOperator(context.CurrentExecutionOperator,
                inputTargetIndex, populatedMetaproperties);
            context.CurrentExecutionOperator = allPropertiesOp;

            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);
            foreach (string metapropertyName in populatedMetaproperties) {
                context.AddField(Alias.Value, metapropertyName, ColumnGraphType.Value);
            }

            return allPropertiesOp;
        }
    }

    partial class WAllValuesTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression inputParameter = Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(inputParameter != null, "inputParameter != null");

            int inputTargetIndex = context.LocateColumnReference(inputParameter);

            AllValuesOperator allValuesOp = new AllValuesOperator(context.CurrentExecutionOperator, inputTargetIndex);
            context.CurrentExecutionOperator = allValuesOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return allValuesOp;
        }
    }

    partial class WPropertiesTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            List<int> propertiesIndex = new List<int>();
            List<string> populateMetaproperties = new List<string>();

            foreach (WScalarExpression expression in Parameters)
            {
                WColumnReferenceExpression targetParameter = expression as WColumnReferenceExpression;
                if (targetParameter != null)
                {
                    propertiesIndex.Add(context.LocateColumnReference(targetParameter));
                    continue;
                }

                WValueExpression populateMetapropertyNameParameter = expression as WValueExpression;
                if (populateMetapropertyNameParameter != null)
                {
                    populateMetaproperties.Add(populateMetapropertyNameParameter.Value);
                    continue;
                }

                throw new QueryCompilationException(
                    "Parameters of Properties table can only be WColumnReferenceExpression or WValueExpression.");

            }

            GraphViewExecutionOperator propertiesOp = new PropertiesOperator(context.CurrentExecutionOperator,
                propertiesIndex, populateMetaproperties);
            context.CurrentExecutionOperator = propertiesOp;

            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);
            foreach (string metapropertyName in populateMetaproperties) {
                context.AddField(Alias.Value, metapropertyName, ColumnGraphType.Value);
            }
        
            return propertiesOp;
        }
    }

    partial class WDedupGlobalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            List<ScalarFunction> targetValueFunctionList =
                this.Parameters.Select(expression => expression.CompileToFunction(context, command)).ToList();

            DeduplicateOperator dedupOp = context.InBatchMode
                ? new DeduplicateInBatchOperator(context.CurrentExecutionOperator, targetValueFunctionList)
                : new DeduplicateOperator(context.CurrentExecutionOperator, targetValueFunctionList);
            context.CurrentExecutionOperator = dedupOp;

            return dedupOp;
        }
    }

    partial class WDedupLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            DeduplicateLocalOperator dedupLocalOp = new DeduplicateLocalOperator(context.CurrentExecutionOperator,
                Parameters[0].CompileToFunction(context, command));
            context.CurrentExecutionOperator = dedupLocalOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return dedupLocalOp;
        }
    }

    partial class WConstantReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            List<ScalarFunction> constantValues = new List<ScalarFunction>();

            foreach (WScalarExpression expression in this.Parameters)
            {
                WValueExpression constantValue = expression as WValueExpression;
                Debug.Assert(constantValue != null, "constantValue != null");
                constantValues.Add(constantValue.CompileToFunction(context, command));
            }

            ConstantOperator constantOp = new ConstantOperator(context.CurrentExecutionOperator, constantValues,
                this.IsList, GremlinKeyword.TableDefaultColumnName);
            context.CurrentExecutionOperator = constantOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return constantOp;
        }
    }

    partial class WProjectTableReference
    {
        private const int StartParameterIndex = 0;
        private const int ParameterStep = 2;

        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            ProjectByOperator projectByOp = new ProjectByOperator(context.CurrentExecutionOperator);

            for (int i = StartParameterIndex; i < this.Parameters.Count; i += ParameterStep)
            {
                WScalarSubquery scalarSubquery = this.Parameters[i] as WScalarSubquery;
                if (scalarSubquery == null)
                    throw new SyntaxErrorException("The parameter of ProjectTableReference at an odd position has to be a WScalarSubquery.");

                WValueExpression projectName = this.Parameters[i + 1] as WValueExpression;
                if (projectName == null)
                    throw new SyntaxErrorException("The parameter of ProjectTableReference at an even position has to be a WValueExpression.");

                ScalarFunction byFunction = scalarSubquery.CompileToFunction(context, command);

                projectByOp.AddProjectBy(projectName.Value, byFunction);
            }

            context.CurrentExecutionOperator = projectByOp;
            context.AddField(this.Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return projectByOp;
        }
    }

    partial class WRepeatTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WSelectQueryBlock contextSelect, repeatSelect;
            Split(out contextSelect, out repeatSelect);

            Container initialContainer = new Container();
            int initialContainerIndex = context.AddContainers(initialContainer);
            QueryCompilationContext initialContext = new QueryCompilationContext(context);
            initialContext.OuterContextOp.SetContainer(initialContainer, initialContainerIndex);
            initialContext.InBatchMode = context.InBatchMode;
            initialContext.CarryOn = true;
            GraphViewExecutionOperator getInitialRecordOp = contextSelect.Compile(initialContext, command);
            
            QueryCompilationContext rTableContext = new QueryCompilationContext(context);

            foreach (WSelectElement selectElement in contextSelect.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                Debug.Assert(selectScalar != null,
                    "The SELECT elements of the sub-queries in a repeat table reference must be select scalar elements.");
                Debug.Assert(selectScalar.ColumnName != null, "selectScalar.ColumnName != null");

                rTableContext.AddField("R", selectScalar.ColumnName, ColumnGraphType.Value);
            }

            WRepeatConditionExpression repeatCondition = Parameters[1] as WRepeatConditionExpression;
            if (repeatCondition == null)
                throw new SyntaxErrorException("The second parameter of a repeat table reference must be WRepeatConditionExpression");

            int repeatTimes = repeatCondition.RepeatTimes;
            bool untilFront = repeatCondition.StartFromContext;
            bool emitFront = repeatCondition.EmitContext;

            // compile until
            BooleanFunction terminationCondition = repeatCondition.TerminationCondition?.CompileToBatchFunction(rTableContext, command);

            // compile emit
            BooleanFunction emitCondition = repeatCondition.EmitCondition?.CompileToBatchFunction(rTableContext, command);
            
            // compile sub-traversal
            Container innerContainer = new Container();
            int innerContainerIndex = context.AddContainers(innerContainer);
            rTableContext.OuterContextOp.SetContainer(innerContainer, innerContainerIndex);
            rTableContext.InBatchMode = context.InBatchMode;
            rTableContext.CarryOn = true;
            GraphViewExecutionOperator innerOp = repeatSelect.Compile(rTableContext, command);

            RepeatOperator repeatOp = new RepeatOperator(
                context.CurrentExecutionOperator,
                initialContainer,
                initialContainerIndex,
                getInitialRecordOp,
                innerContainer,
                innerContainerIndex,
                innerOp,
                emitCondition,
                emitFront,
                terminationCondition,
                untilFront,
                repeatTimes);

            context.CurrentExecutionOperator = repeatOp;

            //
            // Updates the raw record layout
            //
            foreach (WSelectElement selectElement in contextSelect.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                context.AddField(Alias.Value, selectScalar.ColumnName, ColumnGraphType.Value);
            }

            return repeatOp;
        }
    }

    partial class WUnfoldTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            List<string> unfoldColumns = new List<string>() { GremlinKeyword.TableDefaultColumnName };

            for (int i = 1; i < this.Parameters.Count; i++)
            {
                WValueExpression unfoldColumn = this.Parameters[i] as WValueExpression;
                Debug.Assert(unfoldColumn != null, "unfoldColumn != null");
                unfoldColumns.Add(unfoldColumn.Value);
            }

            UnfoldOperator unfoldOp = new UnfoldOperator(
                context.CurrentExecutionOperator,
                Parameters[0].CompileToFunction(context, command), 
                unfoldColumns);
            context.CurrentExecutionOperator = unfoldOp;

            foreach (string columnName in unfoldColumns) {
                context.AddField(Alias.Value, columnName, ColumnGraphType.Value);
            }

            return unfoldOp;
        }
    }

    partial class WPathTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            List<Tuple<ScalarFunction, bool, HashSet<string>>> pathStepList;
            List<ScalarFunction> byFuncList;
            WPathTableReference.GetPathStepListAndByFuncList(context, command, this.Parameters, 
                out pathStepList, out byFuncList);

            PathOperator pathOp = new PathOperator(context.CurrentExecutionOperator, pathStepList, byFuncList);
            context.CurrentExecutionOperator = pathOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return pathOp;
        }

        internal override bool OneLine()
        {
            return false;
        }
    }

    partial class WPath2TableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            //
            // If the boolean value is true, then it's a subPath to be unfolded
            //
            List<Tuple<ScalarFunction, bool>> pathStepList = new List<Tuple<ScalarFunction, bool>>();
            List<ScalarFunction> byFuncList = new List<ScalarFunction>();
            QueryCompilationContext byInitContext = new QueryCompilationContext(context);
            byInitContext.ClearField();
            byInitContext.AddField(GremlinKeyword.Compose1TableDefaultName, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            foreach (WScalarExpression expression in Parameters)
            {
                WFunctionCall basicStep = expression as WFunctionCall;
                WColumnReferenceExpression subPath = expression as WColumnReferenceExpression;
                WScalarSubquery byFunc = expression as WScalarSubquery;

                if (basicStep != null)
                {
                    pathStepList.Add(new Tuple<ScalarFunction, bool>(basicStep.CompileToFunction(context, command), false));
                }
                else if (subPath != null)
                {
                    pathStepList.Add(new Tuple<ScalarFunction, bool>(subPath.CompileToFunction(context, command), true));
                }
                else if (byFunc != null)
                {
                    byFuncList.Add(byFunc.CompileToFunction(byInitContext, command));
                }
                else {
                    throw new QueryCompilationException(
                        "The parameter of WPathTableReference can only be a WFunctionCall/WColumnReferenceExpression/WScalarSubquery.");
                }
            }

            PathOperator2 pathOp = new PathOperator2(context.CurrentExecutionOperator, pathStepList, byFuncList);
            context.CurrentExecutionOperator = pathOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return pathOp;
        }
    }

    partial class WInjectTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression injectColumn = this.Parameters[0] as WColumnReferenceExpression;
            //
            // In g.Inject() case, this injectColumnIndex parameter is useless
            //
            int injectColumnIndex = injectColumn == null ? 0 : context.LocateColumnReference(injectColumn);

            List<ScalarFunction> injectValues = new List<ScalarFunction>();

            for (int i = 1; i < this.Parameters.Count; i++)
            {
                WValueExpression injectValue = this.Parameters[i] as WValueExpression;
                Debug.Assert(injectValue != null, "injectValue != null");
                injectValues.Add(injectValue.CompileToFunction(context, command));

            }

            InjectOperator injectOp = new InjectOperator(context.CurrentExecutionOperator, context.RawRecordLayout.Count, injectColumnIndex,
                injectValues, this.IsList, GremlinKeyword.TableDefaultColumnName);
            context.CurrentExecutionOperator = injectOp;

            //
            // In g.Inject() case, the inject() step creates a new column in RawRecord
            //
            if (context.RawRecordLayout.Count == 0)
                context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return injectOp;
        }
    }

    partial class WAggregateTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WScalarSubquery getAggregateObjectSubqueryParameter = Parameters[0] as WScalarSubquery;
            if (getAggregateObjectSubqueryParameter == null)
                throw new SyntaxErrorException("The first parameter of an Aggregate function must be a WScalarSubquery.");
            ScalarFunction getAggregateObjectFunction = getAggregateObjectSubqueryParameter.CompileToFunction(context, command);

            string storedName = (Parameters[1] as WValueExpression).Value;

            AggregateState sideEffectState;
            if (!context.SideEffectStates.TryGetValue(storedName, out sideEffectState))
            {
                sideEffectState = new CollectionState(this.Alias.Value);
                context.SideEffectStates.Add(storedName, sideEffectState);
            }
            else if (!(sideEffectState is CollectionState))
            {
                if (sideEffectState is GroupState)
                {
                    throw new QueryCompilationException("It's illegal to use the same sideEffect key of a group(string) step and an aggregate(string) step!");
                }
                else
                {
                    throw new QueryCompilationException("It's illegal to use the same sideEffect key of a tree(string) step and an aggregate(string) step!");
                }
            }

            CollectionFunction aggregateFunction = new CollectionFunction(sideEffectState as CollectionState);
            context.SideEffectFunctions[storedName] = aggregateFunction;
            AggregateOperator aggregateOp = new AggregateOperator(context.CurrentExecutionOperator, getAggregateObjectFunction,
                aggregateFunction, storedName);
            context.CurrentExecutionOperator = aggregateOp;
            // TODO: Change to correct ColumnGraphType
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return aggregateOp;
        }
    }

    partial class WStoreTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WScalarSubquery getStoreObjectSubqueryParameter = Parameters[0] as WScalarSubquery;
            if (getStoreObjectSubqueryParameter == null)
                throw new SyntaxErrorException("The first parameter of a Store function must be a WScalarSubquery.");
            ScalarFunction getStoreObjectFunction = getStoreObjectSubqueryParameter.CompileToFunction(context, command);

            string storedName = (Parameters[1] as WValueExpression).Value;

            AggregateState sideEffectState;
            if (!context.SideEffectStates.TryGetValue(storedName, out sideEffectState))
            {
                sideEffectState = new CollectionState(this.Alias.Value);
                context.SideEffectStates.Add(storedName, sideEffectState);
            }
            else if (!(sideEffectState is CollectionState))
            {
                if (sideEffectState is GroupState)
                {
                    throw new QueryCompilationException("It's illegal to use the same sideEffect key of a group(string) step and an aggregate(string) step!");
                }
                else
                {
                    throw new QueryCompilationException("It's illegal to use the same sideEffect key of a tree(string) step and an aggregate(string) step!");
                }
            }
            CollectionFunction storeFunction = new CollectionFunction(sideEffectState as CollectionState);
            context.SideEffectFunctions[storedName] = storeFunction;
            StoreOperator storeOp = new StoreOperator(context.CurrentExecutionOperator, getStoreObjectFunction,
                storeFunction, storedName);
            context.CurrentExecutionOperator = storeOp;
            // TODO: Change to correct ColumnGraphType
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);


            return storeOp;
        }
    }

    partial class WSubgraphTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WScalarSubquery getSubgraphObjectSubqueryParameter = Parameters[0] as WScalarSubquery;
            if (getSubgraphObjectSubqueryParameter == null)
                throw new SyntaxErrorException("The first parameter of a Store function must be a WScalarSubquery.");
            ScalarFunction getSubgraphObjectFunction = getSubgraphObjectSubqueryParameter.CompileToFunction(context, command);
            
            string sideEffectKey = (Parameters[1] as WValueExpression).Value;

            AggregateState sideEffectState;
            if (!context.SideEffectStates.TryGetValue(sideEffectKey, out sideEffectState))
            {
                sideEffectState = new SubgraphState(command, this.Alias.Value);
                context.SideEffectStates.Add(sideEffectKey, sideEffectState);
            }
            else if (!(sideEffectState is SubgraphState))
            {
                if (sideEffectState is GroupState)
                {
                    throw new QueryCompilationException("It's illegal to use the same sideEffect key of a group(string) step and an subgraph(string) step!");
                }
                else if (sideEffectState is TreeState)
                {
                    throw new QueryCompilationException("It's illegal to use the same sideEffect key of a tree(string) step and an subgraph(string) step!");
                }
                else if (sideEffectState is CollectionState)
                {
                    throw new QueryCompilationException("It's illegal to use the same sideEffect key of a aggregate(string) step and an subgraph(string) step!");
                }
                else
                {
                    throw new QueryCompilationException("Unkonw SideEffect");
                }
            }

            SubgraphFunction subgraphFunction = new SubgraphFunction(sideEffectState as SubgraphState);
            context.SideEffectFunctions[sideEffectKey] = subgraphFunction;

            SubgraphOperator subgraphOp = new SubgraphOperator(context.CurrentExecutionOperator, getSubgraphObjectFunction, subgraphFunction, sideEffectKey);
            context.CurrentExecutionOperator = subgraphOp;
            // TODO: Change to correct ColumnGraphType
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);


            return subgraphOp;
        }
    }

    partial class WBarrierTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            var barrierOp = new BarrierOperator(context.CurrentExecutionOperator);
            context.CurrentExecutionOperator = barrierOp;

            return barrierOp;
        }
    }

    partial class WMapTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WScalarSubquery mapSubquery = Parameters[0] as WScalarSubquery;
            if (mapSubquery == null)
            {
                throw new SyntaxErrorException("The input of a map table reference must be a scalar subquery.");
            }
            WSelectQueryBlock mapSelect = mapSubquery.SubQueryExpr as WSelectQueryBlock;
            if (mapSelect == null)
            {
                throw new SyntaxErrorException("The sub-query must be a select query block.");
            }

            Container container = new Container();
            int containerIndex = context.AddContainers(container);
            QueryCompilationContext subcontext = new QueryCompilationContext(context);
            subcontext.OuterContextOp.SetContainer(container, containerIndex);
            subcontext.AddField(GremlinKeyword.IndexTableName, command.IndexColumnName, ColumnGraphType.Value, true);
            subcontext.InBatchMode = true;
            GraphViewExecutionOperator mapTraversalOp = mapSelect.Compile(subcontext, command);
            MapOperator mapOp = new MapOperator(context.CurrentExecutionOperator, mapTraversalOp, container, containerIndex);
            context.CurrentExecutionOperator = mapOp;

            foreach (WSelectElement selectElement in mapSelect.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                if (selectScalar == null)
                {
                    throw new SyntaxErrorException("The SELECT elements of the sub-query in a map table reference must be select scalar elements.");
                }
                Debug.Assert(selectScalar.ColumnName != null, "selectScalar.ColumnName != null");

                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                
                context.AddField(Alias.Value, selectScalar.ColumnName, columnRef?.ColumnGraphType ?? ColumnGraphType.Value);
            }

            return mapOp;
        }
    }

    partial class WSideEffectTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WScalarSubquery sideEffectSubquery = Parameters[0] as WScalarSubquery;
            if (sideEffectSubquery == null)
            {
                throw new SyntaxErrorException("The input of a sideEffect table reference must be a scalar subquery.");
            }
            WSelectQueryBlock sideEffectSelect = sideEffectSubquery.SubQueryExpr as WSelectQueryBlock;
            if (sideEffectSelect == null)
            {
                throw new SyntaxErrorException("The sub-query must be a select query block.");
            }

            Container container = new Container();
            int containerIndex = context.AddContainers(container);
            QueryCompilationContext subcontext = new QueryCompilationContext(context);
            subcontext.OuterContextOp.SetContainer(container, containerIndex);
            subcontext.AddField(GremlinKeyword.IndexTableName, command.IndexColumnName, ColumnGraphType.Value, true);
            subcontext.InBatchMode = true;
            GraphViewExecutionOperator sideEffectTraversalOp = sideEffectSelect.Compile(subcontext, command);
            SideEffectOperator sideEffectOp = new SideEffectOperator(context.CurrentExecutionOperator, sideEffectTraversalOp, container, containerIndex);
            context.CurrentExecutionOperator = sideEffectOp;

            return sideEffectOp;
        }
    }

    partial class WKeyTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression propertyField = Parameters[0] as WColumnReferenceExpression;

            PropertyKeyOperator keyOp = new PropertyKeyOperator(context.CurrentExecutionOperator,
                context.LocateColumnReference(propertyField));
            context.CurrentExecutionOperator = keyOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return keyOp;
        }
    }

    partial class WValueTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression propertyField = Parameters[0] as WColumnReferenceExpression;

            PropertyValueOperator valueOp = new PropertyValueOperator(context.CurrentExecutionOperator,
                context.LocateColumnReference(propertyField));
            context.CurrentExecutionOperator = valueOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return valueOp;
        }
    }

    partial class WTreeTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            string sideEffectKey = (Parameters[0] as WValueExpression).Value;
            WColumnReferenceExpression pathColumn = Parameters[1] as WColumnReferenceExpression;
            Debug.Assert(sideEffectKey != null, "sideEffectKey != null");
            Debug.Assert(pathColumn != null, "pathColumn != null");
            int pathIndex = context.LocateColumnReference(pathColumn);

            AggregateState sideEffectState;
            if (!context.SideEffectStates.TryGetValue(sideEffectKey, out sideEffectState))
            {
                sideEffectState = new TreeState(this.Alias.Value);
                context.SideEffectStates.Add(sideEffectKey, sideEffectState);
            }
            else if (!(sideEffectState is TreeState))
            {
                if (sideEffectState is GroupState)
                {
                    throw new QueryCompilationException("It's illegal to use the same sideEffect key of a group(string) step and a tree(string) step!");
                }
                else
                {
                    throw new QueryCompilationException("It's illegal to use the same sideEffect key of a store/aggregate(string) step and a tree(string) step!");
                }
            }

            TreeFunction treeFunction = new TreeFunction(sideEffectState as TreeState);
            context.SideEffectFunctions[sideEffectKey] = treeFunction;
            TreeSideEffectOperator treeSideEffectOp = new TreeSideEffectOperator(
                context.CurrentExecutionOperator, treeFunction, sideEffectKey, pathIndex);

            context.CurrentExecutionOperator = treeSideEffectOp;

            return treeSideEffectOp;
        }
    }

    partial class WGroupTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WScalarExpression groupKeySubQuery = Parameters[1];
            WScalarSubquery aggregateSubQuery = Parameters[2] as WScalarSubquery;
            Debug.Assert(aggregateSubQuery != null, "aggregateSubQuery != null");

            ScalarFunction groupKeyFunction = groupKeySubQuery.CompileToFunction(context, command);

            QueryCompilationContext subcontext = new QueryCompilationContext(context);
            Container container = new Container();
            int containerIndex = context.AddContainers(container);
            subcontext.OuterContextOp.SetContainer(container, containerIndex);
            GraphViewExecutionOperator aggregateOp = aggregateSubQuery.SubQueryExpr.Compile(subcontext, command);

            WValueExpression groupParameter = Parameters[0] as WValueExpression;
            if (!groupParameter.SingleQuoted && groupParameter.Value.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                GroupOperator groupOp = context.InBatchMode
                    ? new GroupInBatchOperator(
                        context.CurrentExecutionOperator,
                        groupKeyFunction,
                        container, containerIndex, aggregateOp,
                        this.IsProjectingACollection,
                        context.RawRecordLayout.Count)
                    : new GroupOperator(
                        context.CurrentExecutionOperator,
                        groupKeyFunction,
                        container, containerIndex, aggregateOp,
                        this.IsProjectingACollection,
                        context.RawRecordLayout.Count);

                context.CurrentExecutionOperator = groupOp;

                // Change to correct ColumnGraphType
                context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

                return groupOp;
            }
            else
            {
                AggregateState sideEffectState;
                if (!context.SideEffectStates.TryGetValue(groupParameter.Value, out sideEffectState))
                {
                    sideEffectState = new GroupState(this.Alias.Value);
                    context.SideEffectStates.Add(groupParameter.Value, sideEffectState);
                }
                else if (sideEffectState.tableAlias != this.Alias.Value)
                {
                    if (sideEffectState is GroupState)
                    {
                        throw new QueryCompilationException("Multi group with a same sideEffect key is an undefined behavior in Gremlin and hence not supported.");
                    }
                    else if (sideEffectState is TreeState)
                    {
                        throw new QueryCompilationException("It's illegal to use the same sideEffect key of a group(string) step and a tree(string) step!");
                    }
                    else
                    {
                        throw new QueryCompilationException("It's illegal to use the same sideEffect key of a group(string) step and a store/aggregate(string) step!");
                    }
                }

                GroupFunction groupFunction = new GroupFunction(sideEffectState as GroupState, aggregateOp, container,
                    containerIndex, this.IsProjectingACollection);
                context.SideEffectFunctions[groupParameter.Value] = groupFunction;
                GroupSideEffectOperator groupSideEffectOp = new GroupSideEffectOperator(
                    context.CurrentExecutionOperator, groupFunction, groupParameter.Value, groupKeyFunction);

                context.CurrentExecutionOperator = groupSideEffectOp;

                return groupSideEffectOp;
            }
        }
    }

    partial class WQueryDerivedTable
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WSelectQueryBlock derivedSelectQueryBlock = QueryExpr as WSelectQueryBlock;
            if (derivedSelectQueryBlock == null)
                throw new SyntaxErrorException("The QueryExpr of a WQueryDerviedTable must be one select query block.");

            QueryCompilationContext derivedTableContext = new QueryCompilationContext(context);
            Container container = new Container();
            int containerIndex = context.AddContainers(container);

            // If QueryDerivedTable is the first table in the whole script
            if (context.CurrentExecutionOperator == null)
            {
                derivedTableContext.OuterContextOp = null;
            }
            else
            {
                derivedTableContext.InBatchMode = context.InBatchMode;
                derivedTableContext.OuterContextOp.SetContainer(container, containerIndex);
            }
            
            GraphViewExecutionOperator subQueryOp = derivedSelectQueryBlock.Compile(derivedTableContext, command);

            ProjectAggregationInBatch projectAggregationInBatchOp = null;
            if (context.InBatchMode)
            {
                Debug.Assert(subQueryOp is ProjectAggregationInBatch);
                projectAggregationInBatchOp = subQueryOp as ProjectAggregationInBatch;
            }

            QueryDerivedTableOperator queryDerivedTableOp =
                context.InBatchMode
                    ? new QueryDerivedInBatchOperator(context.CurrentExecutionOperator, subQueryOp, container, containerIndex,
                        projectAggregationInBatchOp, context.RawRecordLayout.Count)
                    : new QueryDerivedTableOperator(context.CurrentExecutionOperator, subQueryOp, container, containerIndex,
                        context.RawRecordLayout.Count);

            foreach (var selectElement in derivedSelectQueryBlock.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                if (selectScalar == null)
                {
                    throw new SyntaxErrorException("The inner query of a WQueryDerivedTable can only select scalar elements.");
                }

                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                
                string selectElementAlias = selectScalar.ColumnName;
                if (selectElementAlias == null)
                {
                    WValueExpression expr = selectScalar.SelectExpr as WValueExpression;;
                    if (expr == null)
                        throw new SyntaxErrorException(string.Format("The select element \"{0}\" doesn't have an alias.", selectScalar.ToString()));

                    selectElementAlias = expr.Value;
                }

                context.AddField(Alias.Value, selectElementAlias, columnRef != null ? columnRef.ColumnGraphType : ColumnGraphType.Value);
            }

            context.CurrentExecutionOperator = queryDerivedTableOp;

            return queryDerivedTableOp;
        }
    }

    partial class WSumLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression targetField = Parameters[0] as WColumnReferenceExpression;

            SumLocalOperator sumLocalOp = new SumLocalOperator(context.CurrentExecutionOperator,
                context.LocateColumnReference(targetField));
            context.CurrentExecutionOperator = sumLocalOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return sumLocalOp;
        }
    }

    partial class WMaxLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression targetField = Parameters[0] as WColumnReferenceExpression;

            MaxLocalOperator maxLocalOp = new MaxLocalOperator(context.CurrentExecutionOperator,
                context.LocateColumnReference(targetField));
            context.CurrentExecutionOperator = maxLocalOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return maxLocalOp;
        }
    }

    partial class WMinLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression targetField = Parameters[0] as WColumnReferenceExpression;

            MinLocalOperator minLocalOp = new MinLocalOperator(context.CurrentExecutionOperator,
                context.LocateColumnReference(targetField));
            context.CurrentExecutionOperator = minLocalOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return minLocalOp;
        }
    }

    partial class WMeanLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression targetField = Parameters[0] as WColumnReferenceExpression;

            MeanLocalOperator meanLocalOp = new MeanLocalOperator(context.CurrentExecutionOperator,
                context.LocateColumnReference(targetField));
            context.CurrentExecutionOperator = meanLocalOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return meanLocalOp;
        }
    }

    partial class WCoinTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            GraphViewExecutionOperator inputOp = context.CurrentExecutionOperator;

            double probability = double.Parse(((WValueExpression)this.Parameters[0]).Value);

            GraphViewExecutionOperator coinOp = new CoinOperator(inputOp, probability);
            context.CurrentExecutionOperator = coinOp;
            return coinOp;
        }
    }

    partial class WSampleGlobalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            GraphViewExecutionOperator inputOp = context.CurrentExecutionOperator;
            long amountToSample = long.Parse(((WValueExpression)this.Parameters[0]).Value);
            ScalarFunction byFunction = this.Parameters.Count > 1 
                ? this.Parameters[1].CompileToFunction(context, command) 
                : null;  // Can be null if no "by" step

            GraphViewExecutionOperator sampleOp = new SampleOperator(inputOp, amountToSample, byFunction);
            context.CurrentExecutionOperator = sampleOp;
            return sampleOp;
        }
    }

    partial class WSampleLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression inputObject = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(inputObject != null, "inputObject != null");
            int inputObjectIndex = context.LocateColumnReference(inputObject);

            long amountToSample = long.Parse(((WValueExpression)this.Parameters[1]).Value);

            List<string> populateColumns = new List<string>() { GremlinKeyword.TableDefaultColumnName };

            for (int i = 1; i < this.Parameters.Count; i++)
            {
                WValueExpression populateColumn = this.Parameters[i] as WValueExpression;
                Debug.Assert(populateColumn != null, "populateColumn != null");
                populateColumns.Add(populateColumn.Value);
            }

            GraphViewExecutionOperator sampleOp = new SampleLocalOperator(context.CurrentExecutionOperator, inputObjectIndex, amountToSample, populateColumns);
            context.CurrentExecutionOperator = sampleOp;
            foreach (string columnName in populateColumns)
            {
                context.AddField(Alias.Value, columnName, ColumnGraphType.Value);
            }

            return sampleOp;
        }
    }

    partial class WOrderGlobalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            List<Tuple<ScalarFunction, IComparer>> orderByElements = new List<Tuple<ScalarFunction, IComparer>>();

            foreach (Tuple<WScalarExpression, IComparer> tuple in OrderParameters)
            {
                WScalarExpression byParameter = tuple.Item1;

                ScalarFunction byFunction = byParameter.CompileToFunction(context, command);
                IComparer comparer = tuple.Item2;

                orderByElements.Add(new Tuple<ScalarFunction, IComparer>(byFunction, comparer));
            }

            OrderOperator orderOp = context.InBatchMode
                ? new OrderInBatchOperator(context.CurrentExecutionOperator, orderByElements) 
                : new OrderOperator(context.CurrentExecutionOperator, orderByElements);
            context.CurrentExecutionOperator = orderOp;

            return orderOp;
        }
    }

    partial class WOrderLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression inputObject = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(inputObject != null, "inputObject != null");
            int inputObjectIndex = context.LocateColumnReference(inputObject);

            QueryCompilationContext byInitContext = new QueryCompilationContext(context);
            byInitContext.ClearField();
            byInitContext.AddField(GremlinKeyword.Compose1TableDefaultName, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            List<Tuple<ScalarFunction, IComparer>> orderByElements = new List<Tuple<ScalarFunction, IComparer>>();

            foreach (Tuple<WScalarExpression, IComparer> tuple in OrderParameters)
            {
                WScalarExpression byParameter = tuple.Item1;

                ScalarFunction byFunction = byParameter.CompileToFunction(byInitContext, command);
                IComparer comparer = tuple.Item2;

                orderByElements.Add(new Tuple<ScalarFunction, IComparer>(byFunction, comparer));
            }

            List<string> populateColumns = new List<string> () { GremlinKeyword.TableDefaultColumnName };

            for (int i = this.OrderParameters.Count + 1; i < this.Parameters.Count; i++)
            {
                WValueExpression populateColumn = this.Parameters[i] as WValueExpression;
                Debug.Assert(populateColumn != null, "populateColumn != null");
                populateColumns.Add(populateColumn.Value);
            }

            OrderLocalOperator orderLocalOp = new OrderLocalOperator(context.CurrentExecutionOperator, inputObjectIndex,
                orderByElements, populateColumns);
            context.CurrentExecutionOperator = orderLocalOp;
            foreach (string columnName in populateColumns) {
                context.AddField(Alias.Value, columnName, ColumnGraphType.Value);
            }

            return orderLocalOp;
        }
    }

    partial class WRangeGlobalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            int lowEnd = int.Parse((Parameters[0] as WValueExpression).Value);
            int highEnd = int.Parse((Parameters[1] as WValueExpression).Value);
            int tailFlag = int.Parse((Parameters[2] as WValueExpression).Value);
            bool isTail = tailFlag > 0;

            //
            // Compilation of Tail op, which returns lastN elements
            //
            if (isTail)
            {
                int lastN = highEnd < 0 ? 1 : highEnd;

                TailOperator tailOp = context.InBatchMode
                    ? new TailInBatchOperator(context.CurrentExecutionOperator, lastN)
                    : new TailOperator(context.CurrentExecutionOperator, lastN);
                context.CurrentExecutionOperator = tailOp;

                return tailOp;
            }
            //
            // Compilation of Range op, which return elements from [startIndex, startIndex + count)
            // If count == -1, return all elements starting from startIndex 
            //
            else
            {
                if ((lowEnd > highEnd && highEnd >= 0) || (lowEnd >= 0 && highEnd < -1))
                {
                    throw new QueryCompilationException(string.Format("Not a legal range: [{0}, {1}]", lowEnd, highEnd));
                }

                int startIndex = lowEnd < 0 ? 0 : lowEnd;
                int count;
                if (highEnd == -1)
                {
                    count = -1;
                }
                else if ((count = highEnd - startIndex) < 0)
                {
                    count = 0;
                }
                
                RangeOperator rangeOp = context.InBatchMode
                    ? new RangeInBatchOperator(context.CurrentExecutionOperator, startIndex, count)
                    : new RangeOperator(context.CurrentExecutionOperator, startIndex, count);
                context.CurrentExecutionOperator = rangeOp;

                return rangeOp;
            }
        }
    }

    partial class WRangeLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            //
            // The first parameter is used only when isLocal = true
            //
            WColumnReferenceExpression inputCollection = Parameters[0] as WColumnReferenceExpression;
            int lowEnd = int.Parse((Parameters[1] as WValueExpression).Value);
            int highEnd = int.Parse((Parameters[2] as WValueExpression).Value);
            int tailFlag = int.Parse((Parameters[3] as WValueExpression).Value);
            bool isTail = tailFlag > 0;

            List<string> populateColumns = new List<string> {DocumentDBKeywords.KW_TABLE_DEFAULT_COLUMN_NAME};
            
            //
            // Compilation of Tail op, which returns lastN elements
            //
            if (isTail)
            {
                int lastN = highEnd < 0 ? 1 : highEnd;
                
                TailLocalOperator tailLocalOp = new TailLocalOperator(context.CurrentExecutionOperator,
                    context.LocateColumnReference(inputCollection), lastN, populateColumns);
                context.CurrentExecutionOperator = tailLocalOp;
                foreach (string columnName in populateColumns) {
                    context.AddField(Alias.Value, columnName, ColumnGraphType.Value);
                }

                return tailLocalOp;
            }
            //
            // Compilation of Range op, which return elements from [startIndex, startIndex + count)
            // If count == -1, return all elements starting from startIndex 
            //
            else
            {
                if ((lowEnd > highEnd && highEnd >= 0) || (lowEnd >= 0 && highEnd < -1)) {
                    throw new QueryCompilationException(string.Format("Not a legal range: [{0}, {1}]", lowEnd, highEnd));
                }

                int startIndex = lowEnd < 0 ? 0 : lowEnd;
                int count;
                if (highEnd == -1) {
                    count = -1;
                }
                else if ((count = highEnd - startIndex) < 0) {
                    count = 0;
                }
                
                RangeLocalOperator rangeLocalOp = new RangeLocalOperator(context.CurrentExecutionOperator,
                    context.LocateColumnReference(inputCollection), startIndex, count, populateColumns);

                context.CurrentExecutionOperator = rangeLocalOp;
                foreach (string columnName in populateColumns) {
                    context.AddField(Alias.Value, columnName, ColumnGraphType.Value);
                }

                return rangeLocalOp;
            }
        }
    }

    /// <summary>
    /// The table-valued function that takes as input a CompositeField field in an input record, 
    /// and outputs a new record in which members in the CompositeField field populated as separate
    /// fields/columns. 
    /// </summary>
    partial class WDecomposeTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression decomposeTargetParameter = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(decomposeTargetParameter != null, "decomposeTargetParameter != null");

            int decomposeTargetIndex = context.LocateColumnReference(decomposeTargetParameter);
            List<string> populateColumns = new List<string>() { GremlinKeyword.TableDefaultColumnName };

            for (int i = 1; i < this.Parameters.Count; i++)
            {
                WValueExpression populateColumn = this.Parameters[i] as WValueExpression;
                Debug.Assert(populateColumn != null, "populateColumn != null");

                populateColumns.Add(populateColumn.Value);
            }

            Decompose1Operator decompose1Op = new Decompose1Operator(context.CurrentExecutionOperator,
                decomposeTargetIndex, populateColumns, GremlinKeyword.TableDefaultColumnName);
            context.CurrentExecutionOperator = decompose1Op;

            foreach (string populateColumn in populateColumns) {
                context.AddField(Alias.Value, populateColumn, ColumnGraphType.Value);
            }

            return decompose1Op;
        }
    }

    partial class WSimplePathTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression pathColumn = Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(pathColumn != null, "pathColumn != null");
            int pathIndex = context.LocateColumnReference(pathColumn);

            SimplePathOperator simplePathOp = new SimplePathOperator(context.CurrentExecutionOperator, pathIndex);
            context.CurrentExecutionOperator = simplePathOp;

            return simplePathOp;
        }
    }

    partial class WCyclicPathTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression pathColumn = Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(pathColumn != null, "pathColumn != null");
            int pathIndex = context.LocateColumnReference(pathColumn);

            CyclicPathOperator cyclicPathOp = new CyclicPathOperator(context.CurrentExecutionOperator, pathIndex);
            context.CurrentExecutionOperator = cyclicPathOp;

            return cyclicPathOp;
        }
    }

    partial class WValueMapTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression inputTarget = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(inputTarget != null, "inputTarget != null");
            int inputTargetIndex = context.LocateColumnReference(inputTarget);

            WValueExpression includingMetaParameter = this.Parameters[1] as WValueExpression;
            Debug.Assert(includingMetaParameter != null, "includingMetaParameter != null");
            bool includingMetaValue = int.Parse(includingMetaParameter.Value) > 0;

            List<string> propertyNameList = new List<string>();
            for (int i = 2; i < this.Parameters.Count; i++)
            {
                WValueExpression propertyName = this.Parameters[i] as WValueExpression;
                Debug.Assert(propertyName != null, "propertyName != null");

                propertyNameList.Add(propertyName.Value);
            }

            ValueMapOperator valueMapOp = new ValueMapOperator(context.CurrentExecutionOperator, inputTargetIndex,
                includingMetaValue, propertyNameList);
            context.CurrentExecutionOperator = valueMapOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return valueMapOp;
        }
    }

    partial class WPropertyMapTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression inputTarget = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(inputTarget != null, "inputTarget != null");
            int inputTargetIndex = context.LocateColumnReference(inputTarget);

            List<string> propertyNameList = new List<string>();
            for (int i = 1; i < this.Parameters.Count; i++)
            {
                WValueExpression propertyName = this.Parameters[i] as WValueExpression;
                Debug.Assert(propertyName != null, "propertyName != null");

                propertyNameList.Add(propertyName.Value);
            }

            PropertyMapOperator propertyMapOp = new PropertyMapOperator(context.CurrentExecutionOperator,
                inputTargetIndex, propertyNameList);
            context.CurrentExecutionOperator = propertyMapOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return propertyMapOp;
        }
    }

    partial class WChooseTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WScalarSubquery targetSubquery = this.Parameters[0] as WScalarSubquery;
            Debug.Assert(targetSubquery != null, "targetSubquery != null");

            WScalarSubquery trueTraversalParameter = this.Parameters[1] as WScalarSubquery;
            Debug.Assert(trueTraversalParameter != null, "trueTraversalParameter != null");
            WSelectQueryBlock selectQueryBlock = trueTraversalParameter.SubQueryExpr as WSelectQueryBlock;
            Debug.Assert(selectQueryBlock != null, "selectQueryBlock != null");

            WScalarSubquery falseTraversalParameter = this.Parameters[2] as WScalarSubquery;
            Debug.Assert(falseTraversalParameter != null, "falseTraversalParameter != null");
            
            Container container = new Container();
            int containerIndex = context.AddContainers(container);
            QueryCompilationContext targetSubContext = new QueryCompilationContext(context);
            targetSubContext.OuterContextOp.SetContainer(container, containerIndex);
            targetSubContext.AddField(GremlinKeyword.IndexTableName, command.IndexColumnName, ColumnGraphType.Value, true);
            targetSubContext.InBatchMode = true;
            GraphViewExecutionOperator targetSubqueryOp = targetSubquery.SubQueryExpr.Compile(targetSubContext, command);

            Container trueBranchContainer = new Container();
            int trueBranchContainerIndex = context.AddContainers(trueBranchContainer);
            QueryCompilationContext trueSubContext = new QueryCompilationContext(context);
            trueSubContext.CarryOn = true;
            trueSubContext.InBatchMode = context.InBatchMode;
            trueSubContext.OuterContextOp.SetContainer(trueBranchContainer, trueBranchContainerIndex);
            GraphViewExecutionOperator trueBranchTraversalOp =
                trueTraversalParameter.SubQueryExpr.Compile(trueSubContext, command);

            Container falseBranchContainer = new Container();
            int falseBranchContainerIndex = context.AddContainers(falseBranchContainer);
            QueryCompilationContext falseSubContext = new QueryCompilationContext(context);
            falseSubContext.CarryOn = true;
            falseSubContext.InBatchMode = context.InBatchMode;
            falseSubContext.OuterContextOp.SetContainer(falseBranchContainer, falseBranchContainerIndex);
            GraphViewExecutionOperator falseBranchTraversalOp =
                falseTraversalParameter.SubQueryExpr.Compile(falseSubContext, command);

            ChooseOperator chooseOp = new ChooseOperator(
                context.CurrentExecutionOperator,
                container, containerIndex,
                targetSubqueryOp,
                trueBranchContainer, trueBranchContainerIndex, trueBranchTraversalOp, 
                falseBranchContainer, falseBranchContainerIndex, falseBranchTraversalOp);
            context.CurrentExecutionOperator = chooseOp;

            foreach (WSelectElement selectElement in selectQueryBlock.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                Debug.Assert(selectScalar != null, "selectScalar != null");
                Debug.Assert(selectScalar.ColumnName != null, "selectScalar.ColumnName != null");

                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                
                context.AddField(Alias.Value, selectScalar.ColumnName, columnRef?.ColumnGraphType ?? ColumnGraphType.Value);
            }

            return chooseOp;
        }
    }

    partial class WChooseWithOptionsTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WScalarSubquery targetSubquery = this.Parameters[0] as WScalarSubquery;
            Debug.Assert(targetSubquery != null, "targetSubquery != null");

            Container container = new Container();
            int containerIndex = context.AddContainers(container);
            QueryCompilationContext targetContext = new QueryCompilationContext(context);
            targetContext.InBatchMode = true;
            targetContext.OuterContextOp.SetContainer(container, containerIndex);
            targetContext.AddField(GremlinKeyword.IndexTableName, command.IndexColumnName, ColumnGraphType.Value, true);
            GraphViewExecutionOperator targetSubqueryOp = targetSubquery.SubQueryExpr.Compile(targetContext, command);

            ChooseWithOptionsOperator chooseWithOptionsOp =
                new ChooseWithOptionsOperator(
                    context.CurrentExecutionOperator,
                    container, 
                    containerIndex,
                    targetSubqueryOp);

            WSelectQueryBlock firstSelectQuery = null;
            for (int i = 1; i < this.Parameters.Count; i += 2)
            {
                WValueExpression value = this.Parameters[i] as WValueExpression;
                Debug.Assert(value != null, "value != null");
                if (this.IsOptionNone(value)) {
                    value = null;
                }

                WScalarSubquery scalarSubquery = this.Parameters[i + 1] as WScalarSubquery;
                Debug.Assert(scalarSubquery != null, "scalarSubquery != null");

                if (firstSelectQuery == null)
                {
                    firstSelectQuery = scalarSubquery.SubQueryExpr as WSelectQueryBlock;
                    Debug.Assert(firstSelectQuery != null, "firstSelectQuery != null");
                }

                Container optionContainer = new Container();
                int optionContainerIndex = context.AddContainers(optionContainer);
                QueryCompilationContext subcontext = new QueryCompilationContext(context);
                subcontext.CarryOn = true;
                subcontext.InBatchMode = context.InBatchMode;
                subcontext.OuterContextOp.SetContainer(optionContainer, optionContainerIndex);
                GraphViewExecutionOperator optionTraversalOp = scalarSubquery.SubQueryExpr.Compile(subcontext, command);
                chooseWithOptionsOp.AddOptionTraversal(value?.CompileToFunction(context, command), optionContainer, optionTraversalOp, optionContainerIndex);
            }

            foreach (WSelectElement selectElement in firstSelectQuery.SelectElements)
            {
                WSelectScalarExpression selectScalar = selectElement as WSelectScalarExpression;
                Debug.Assert(selectScalar != null, "selectScalar != null");
                Debug.Assert(selectScalar.ColumnName != null, "selectScalar.ColumnName != null");

                WColumnReferenceExpression columnRef = selectScalar.SelectExpr as WColumnReferenceExpression;
                
                context.AddField(Alias.Value, selectScalar.ColumnName, columnRef?.ColumnGraphType ?? ColumnGraphType.Value);
            }

            context.CurrentExecutionOperator = chooseWithOptionsOp;
            return chooseWithOptionsOp;
        }
    }

    /// <summary>
    /// This table-valued function is for Map.select(keys) or Map.select(values)
    /// </summary>
    partial class WSelectColumnTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            // inputTargetParameter always points to MapField
            WColumnReferenceExpression inputTargetParameter = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(inputTargetParameter != null, "inputTargetParameter != null");
            int inputTargetIndex = context.LocateColumnReference(inputTargetParameter);

            // Whether extracts keys or values from MapField
            WValueExpression selectParameter = this.Parameters[1] as WValueExpression;
            Debug.Assert(selectParameter != null, "selectParameter != null");
            bool isSelectKeys = selectParameter.Value.Equals("keys", StringComparison.OrdinalIgnoreCase);
            List<string> populateColumns = new List<string>() { GremlinKeyword.TableDefaultColumnName };

            for (int i = 2; i < this.Parameters.Count; i++)
            {
                WValueExpression populateParameter = this.Parameters[i] as WValueExpression;
                Debug.Assert(populateParameter != null, "populateParameter != null");
                populateColumns.Add(populateParameter.Value);
            }

            SelectColumnOperator selectColumnOp = new SelectColumnOperator(context.CurrentExecutionOperator,
                inputTargetIndex, isSelectKeys, populateColumns);
            context.CurrentExecutionOperator = selectColumnOp;
            foreach (string populateColumnName in populateColumns)
            {
                context.AddField(Alias.Value, populateColumnName, ColumnGraphType.Value);
            }

            return selectColumnOp;
        }
    }

    /// <summary>
    /// This TVF is for select() of more than one key. The result of this function is MapField.
    /// </summary>
    partial class WSelectTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression inputObjectParameter = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(inputObjectParameter != null, "inputObjectParameter != null");
            int inputObjectIndex = context.LocateColumnReference(inputObjectParameter);

            WColumnReferenceExpression pathParameter = this.Parameters[1] as WColumnReferenceExpression;
            Debug.Assert(pathParameter != null, "pathParameter != null");
            int pathIndex = context.LocateColumnReference(pathParameter);

            WValueExpression popParameter = this.Parameters[2] as WValueExpression;
            Debug.Assert(popParameter != null, "popParameter != null");
            GremlinKeyword.Pop popType;
            if (!Enum.TryParse(popParameter.Value, true, out popType))
                throw new QueryCompilationException("Unsupported pop type.");

            List<ScalarFunction> byFuncList = new List<ScalarFunction>();
            QueryCompilationContext byInitContext = new QueryCompilationContext(context);
            byInitContext.ClearField();
            byInitContext.AddField(GremlinKeyword.Compose1TableDefaultName, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            List<string> selectLabels = new List<string>();

            for (int i = 3; i < this.Parameters.Count; i++)
            {
                WValueExpression label = this.Parameters[i] as WValueExpression;
                WScalarSubquery byFunc = this.Parameters[i] as WScalarSubquery;

                if (label != null) {
                    selectLabels.Add(label.Value);
                }
                else if (byFunc != null) {
                    byFuncList.Add(byFunc.CompileToFunction(byInitContext, command));
                }
                else {
                    throw new QueryCompilationException(
                        "The parameter of WSelectTableReference can only be a WValueExpression or WScalarSubquery.");
                }
            }
            
            SelectOperator selectOp = new SelectOperator(
                context.CurrentExecutionOperator,
                inputObjectIndex,
                pathIndex,
                popType,
                selectLabels,
                byFuncList,
                GremlinKeyword.TableDefaultColumnName);
            context.SelectOperators.Add(selectOp);
            context.CurrentExecutionOperator = selectOp;
            context.AddField(Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return selectOp;
        }
    }

    /// <summary>
    /// The TVF is for select() of a single key. The result of the function follows the following precedence:
    /// 
    /// 1) if there is a global table tagged with key through store()/aggregate()/group()/groupCount()/tree(),
    /// return for each record a new record with an additional field containing the content of the table.
    /// 2) if the prior step returns MapField, select the key from the map.   
    /// 3) Otherwise, 
    ///     when the key appears only once, the result is 
    ///     a record in which the tagged (composite) field's elements are populated as individual fields. 
    ///     When the key appears more than once, the result is an array (CollectionField) with each array element
    ///     being tagged CompositeField. 
    /// </summary>
    partial class WSelectOneTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            // inputObjectParameter points to the table reference right before WPathTableReference
            WColumnReferenceExpression inputObjectParameter = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(inputObjectParameter != null, "inputObjectParameter != null");
            int inputObjectIndex = context.LocateColumnReference(inputObjectParameter);

            // WPathTableReference always proceeds WSelectOneTableReference in the FROM clause
            WColumnReferenceExpression pathParameter = this.Parameters[1] as WColumnReferenceExpression;
            Debug.Assert(pathParameter != null, "pathParameter != null");
            int pathIndex = context.LocateColumnReference(pathParameter);

            WValueExpression popParameter = this.Parameters[2] as WValueExpression;
            Debug.Assert(popParameter != null, "popParameter != null");
            GremlinKeyword.Pop popType;
            if (!Enum.TryParse(popParameter.Value, true, out popType))
                throw new QueryCompilationException("Unsupported pop type.");


            WValueExpression labelParameter = this.Parameters[3] as WValueExpression;
            Debug.Assert(labelParameter != null, "labelParameter != null");
            string selectLabel = labelParameter.Value;

            WScalarSubquery byParameter = this.Parameters[4] as WScalarSubquery;
            Debug.Assert(byParameter != null, "byParameter != null");

            QueryCompilationContext byInitContext = new QueryCompilationContext(context);
            byInitContext.ClearField();
            byInitContext.AddField(GremlinKeyword.Compose1TableDefaultName, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);
            ScalarFunction byFunc = byParameter.CompileToFunction(byInitContext, command);
            
            List<string> populateColumns = new List<string>() { GremlinKeyword.TableDefaultColumnName };

            for (int i = 5; i < this.Parameters.Count; i++)
            {
                WValueExpression populateColumnParameter = this.Parameters[i] as WValueExpression;
                Debug.Assert(populateColumnParameter != null, "populateColumnParameter != null");
                
                populateColumns.Add(populateColumnParameter.Value);
            }

            SelectOneOperator selectOneOp = new SelectOneOperator(
                context.CurrentExecutionOperator,
                inputObjectIndex,
                pathIndex,
                popType,
                selectLabel,
                byFunc,
                populateColumns,
                GremlinKeyword.TableDefaultColumnName
                );
            context.SelectOperators.Add(selectOneOp);
            context.CurrentExecutionOperator = selectOneOp;
            foreach (string columnName in populateColumns) {
                context.AddField(Alias.Value, columnName, ColumnGraphType.Value);
            }

            return selectOneOp;
        }
    }

    partial class WCountLocalTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            WColumnReferenceExpression inputObjectParameter = this.Parameters[0] as WColumnReferenceExpression;
            Debug.Assert(inputObjectParameter != null, "inputObjectParameter != null");
            int inputObjectIndex = context.LocateColumnReference(inputObjectParameter);

            CountLocalOperator countLocalOp = new CountLocalOperator(context.CurrentExecutionOperator, inputObjectIndex);
            context.CurrentExecutionOperator = countLocalOp;
            context.AddField(this.Alias.Value, GremlinKeyword.TableDefaultColumnName, ColumnGraphType.Value);

            return countLocalOp;
        }
    }

    partial class WFilterTableReference
    {
        internal override GraphViewExecutionOperator Compile(QueryCompilationContext context, GraphViewCommand command)
        {
            Debug.Assert(Parameters.Count == 1);
            WSearchedCaseExpression caseExpression = Parameters[0] as WSearchedCaseExpression;
            Debug.Assert(caseExpression!=null && caseExpression.WhenClauses.Count==1);

            WBooleanExpression condition = caseExpression.WhenClauses[0].WhenExpression;

            BooleanFunction func = context.InBatchMode
                ? condition.CompileToBatchFunction(context, command)
                : condition.CompileToFunction(context, command);
            GraphViewExecutionOperator filterOp = context.InBatchMode
                ? (GraphViewExecutionOperator) new FilterInBatchOperator(context.CurrentExecutionOperator, func)
                : new FilterOperator(context.CurrentExecutionOperator, func);

            context.CurrentExecutionOperator = filterOp;
            return filterOp;
        }
    }
}

