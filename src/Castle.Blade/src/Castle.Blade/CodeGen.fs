﻿//  Copyright 2004-2011 Castle Project - http://www.castleproject.org/
//  Hamilton Verissimo de Oliveira and individual contributors as indicated. 
//  See the committers.txt/contributors.txt in the distribution for a 
//  full listing of individual contributors.
// 
//  This is free software; you can redistribute it and/or modify it
//  under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 3 of
//  the License, or (at your option) any later version.
// 
//  You should have received a copy of the GNU Lesser General Public
//  License along with this software; if not, write to the Free
//  Software Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA
//  02110-1301 USA, or see the FSF site: http://www.fsf.org.

namespace Castle.Blade

    open System
    open System.Collections.Generic
    open FParsec

    type CodeGenOptions (renderMethod:string, writeLiteral:string, write:string) = 
        let _imports = List<string>()
        let mutable _renderMethod = renderMethod
        let mutable _writeLiteral = writeLiteral
        let mutable _write = write
        let mutable _defNamespace = "Blade"
        let mutable _defBaseClass = "Castle.Blade.BaseBladePage"

        new () = CodeGenOptions("RenderPage", "WriteLiteral", "Write")

        member x.Imports = _imports

        member x.DefaultNamespace
            with get() = _defNamespace and set v = _defNamespace <- v
        
        member x.DefaultBaseClass
            with get() = _defBaseClass and set v = _defBaseClass <- v

        member x.RenderMethodName
            with get() = _renderMethod and set v = _renderMethod <- v

        member x.WriteLiteralMethodName  
            with get() = _writeLiteral and set v = _writeLiteral <- v

        member x.WriteMethodName  
            with get() = _write and set v = _write <- v


    module CodeGen = 

        open AST
        open System
        open System.IO
        open System.CodeDom
        open System.Collections.Generic
        open System.Text
        open System.CodeDom.Compiler
        open Microsoft.CSharp
        open Castle.Blade

        type internal StmtCollWrapper(addStmt:CodeStatement -> unit) =
            class
                let _buffer = StringBuilder()

                member x.AddLine(content:string) = 
                    let lines = content.Split ([|System.Environment.NewLine|], StringSplitOptions.RemoveEmptyEntries)
                    for line in lines do
                        _buffer.Append line |> ignore

                member x.AddExp (exp:CodeExpression) = 
                    x.Add (CodeExpressionStatement exp)
                
                member x.Add(stmt:CodeStatement) = 
                    x.Flush()
                    addStmt(stmt)

                member x.AddAll(stmts:CodeStatement seq) = 
                    x.Flush()
                    for s in stmts do
                        addStmt(s)

                member x.Flush() = 
                    if _buffer.ToString().Trim().Length <> 0 then 
                        let lines = _buffer.ToString().Split ([|System.Environment.NewLine|], StringSplitOptions.RemoveEmptyEntries)
                        for line in lines do 
                            addStmt(CodeSnippetStatement(line))
                        _buffer.Length <- 0

                static member FromMethod(cmethod:CodeMemberMethod) = 
                    StmtCollWrapper(fun s -> cmethod.Statements.Add s |> ignore)
                static member FromList(list:List<CodeStatement>) = 
                    StmtCollWrapper(fun s -> list.Add s)
                static member FromBuffer(buf:StringBuilder) = 
                    let provider = new CSharpCodeProvider()
                    let opts = new CodeGeneratorOptions ( IndentString = "    ", BracingStyle = "C" )
                    StmtCollWrapper(
                        fun s -> 
                            use writer = new StringWriter()
                            provider.GenerateCodeFromStatement(s, writer, opts)
                            // .TrimEnd([|'\r';'\n';'\t'|])
                            buf.Append (writer.GetStringBuilder().ToString()) |> ignore
                    )
            end

        let build_compilation_unit (typeDecl:CodeTypeDeclaration) (options:CodeGenOptions) = 
            let compileUnit = CodeCompileUnit()
            
            let rootNs = CodeNamespace(options.DefaultNamespace)
            compileUnit.Namespaces.Add rootNs |> ignore

            for name in options.Imports do
                rootNs.Imports.Add (CodeNamespaceImport name) |> ignore
            
            typeDecl.BaseTypes.Add (CodeTypeReference options.DefaultBaseClass) |> ignore

            rootNs.Types.Add typeDecl |> ignore

            (compileUnit, rootNs)

        let internal textWriterName = "__writer"

        let internal writeLiteralContent (content:string) 
                                         (writeLiteralMethod:CodeMethodReferenceExpression) 
                                         (lambdaDepth:int) (stmtColl:StmtCollWrapper) (pos:Position option) =
            if not (String.IsNullOrEmpty content) then
                let args : CodeExpression seq = seq {
                                if lambdaDepth <> 0 then yield upcast CodeVariableReferenceExpression( textWriterName + lambdaDepth.ToString() )
                                yield upcast CodeSnippetExpression("\"" + content + "\"") 
                           }
                let writeContent = CodeMethodInvokeExpression(writeLiteralMethod, args |> Seq.toArray  )
                let stmt = CodeExpressionStatement writeContent
                if pos.IsSome then
                    stmt.LinePragma <- CodeLinePragma(pos.Value.StreamName, int(pos.Value.Line))
                stmtColl.Add stmt

        let internal writeCodeContent (codeExp:string) 
                                      (writeMethod:CodeMethodReferenceExpression) 
                                      (lambdaDepth:int) (stmtColl:StmtCollWrapper) (pos:Position option) =
            if not (String.IsNullOrEmpty codeExp) then
                let args : CodeExpression seq = seq {
                                if lambdaDepth <> 0 then yield upcast CodeSnippetExpression( textWriterName + lambdaDepth.ToString() )
                                yield upcast CodeSnippetExpression(codeExp) 
                           }
                let writeContent = CodeMethodInvokeExpression(writeMethod, args |> Seq.toArray  )
                let stmt = CodeExpressionStatement writeContent
                //if pos.IsSome then
                //    stmt.LinePragma <- CodeLinePragma(pos.Value.StreamName, int(pos.Value.Line))
                
                stmtColl.Add stmt



        let rec internal gen_code (node:ASTNode) (rootNs:CodeNamespace) (typeDecl:CodeTypeDeclaration) (compUnit:CodeCompileUnit)  
                                  (stmtCollArg:StmtCollWrapper) 
                                  (writeLiteralMethod:CodeMethodReferenceExpression) (writeMethod:CodeMethodReferenceExpression)
                                  (withinCode:bool) (lambdaDepth:int) = 

            let rec fold_suffix (stmts:StmtCollWrapper) (lst:ASTNode list) = 
                for s in lst do
                    match s with
                    | Invocation (name,next) -> 
                        stmts.AddLine "."
                        stmts.AddLine name
                        if next.IsSome then
                            fold_suffix stmts [next.Value]
                    | Bracket (content, next) ->
                        stmts.AddLine "["
                        stmts.AddLine content
                        stmts.AddLine "]"
                        if next.IsSome then
                            fold_suffix stmts [next.Value]
                    | Param sndlst ->
                        stmts.AddLine "("
                        sndlst |> Seq.iter (fun n -> gen_code n rootNs typeDecl compUnit stmts writeLiteralMethod writeMethod true lambdaDepth)
                        stmts.AddLine ")"
                    | Code (p, c) ->
                        stmts.AddLine c
                    | Comment -> ()
                    | _ ->  failwithf "which node is that? %O" (s.GetType())
            let fold_into_array (node:ASTNode) = 
                let list = List<CodeStatement>()
                let stmtColl = StmtCollWrapper.FromList list
                gen_code node rootNs typeDecl compUnit stmtColl writeLiteralMethod writeMethod true lambdaDepth
                stmtColl.Flush()
                list.ToArray()
            let fold_params_into_buf (node:ASTNode) = 
                match node with
                | Param lst -> 
                    let buf = StringBuilder()
                    let stmtColl = StmtCollWrapper.FromBuffer buf
                    stmtColl.AddLine "("
                    lst |> Seq.iter (fun n -> gen_code n rootNs typeDecl compUnit stmtColl writeLiteralMethod writeMethod true lambdaDepth)
                    stmtColl.AddLine ")"
                    stmtColl.Flush()
                    buf.ToString()
                | _ -> 
                    failwithf "Cannot fold into exp node %O. Expected Params node instead" node
            let fold_into_buf (node:ASTNode) = 
                let buf = StringBuilder()
                let stmtColl = StmtCollWrapper.FromBuffer buf
                gen_code node rootNs typeDecl compUnit stmtColl writeLiteralMethod writeMethod true lambdaDepth
                stmtColl.Flush()
                buf.ToString()

            match node with
            | Markup (pos, content) -> // of string
                writeLiteralContent content writeLiteralMethod lambdaDepth stmtCollArg (Some pos)

            | MarkupBlock lst -> // of ASTNode list
                lst |> 
                    Seq.iter (fun n -> gen_code n rootNs typeDecl compUnit stmtCollArg writeLiteralMethod writeMethod false lambdaDepth)
            
            | MarkupWithinElement (tagNode, nds) -> // of string * ASTNode list
                let stmts1 = fold_into_array tagNode
                let stmts2 = fold_into_array nds
                let nodes = (Array.append stmts1 stmts2)
                stmtCollArg.AddAll nodes

            | Code (p, codeContent) -> // of string
                if withinCode then
                    stmtCollArg.AddLine codeContent
                else
                    writeCodeContent codeContent writeMethod lambdaDepth stmtCollArg (Some p)
            
            | CodeBlock lst -> // of ASTNode list
                for n in lst do
                    gen_code n rootNs typeDecl compUnit stmtCollArg writeLiteralMethod writeMethod true lambdaDepth
        
            | Lambda (paramnames, nd) -> // of ASTNode
                stmtCollArg.AddLine (sprintf "%s => new HtmlResult(%s%d => { \r\n" (List.head paramnames) textWriterName (lambdaDepth + 1))
                gen_code nd rootNs typeDecl compUnit stmtCollArg writeLiteralMethod writeMethod true (lambdaDepth + 1)
                stmtCollArg.AddLine "})"

            | Invocation (left, nd) -> // of string * ASTNode
                let buf = StringBuilder()
                let newstmts = StmtCollWrapper.FromBuffer buf
                newstmts.AddLine left
                if nd.IsSome then fold_suffix newstmts [nd.Value]
                newstmts.Flush()
                writeCodeContent (buf.ToString()) writeMethod lambdaDepth stmtCollArg None

            | IfElseBlock (cond, trueBlock, otherBlock) -> // of ASTNode * ASTNode * ASTNode option
                let condition = fold_params_into_buf cond
                let trueStmts = fold_into_array trueBlock
                let falseStmts = if otherBlock.IsSome then fold_into_array otherBlock.Value else Array.empty
                stmtCollArg.Add (CodeConditionStatement(CodeSnippetExpression(condition), trueStmts, falseStmts))

            | Param lst -> // of ASTNode list
                failwith "should not bump into Param during gen_code"
        
            | KeywordBlock (id, name, block) -> // of string * ASTNode
                if id = "section" then
                    // DefineSection ("test", (writer) => { });
                    let buf = StringBuilder()
                    let block_stmts = StmtCollWrapper.FromBuffer buf
                    gen_code block rootNs typeDecl compUnit block_stmts writeLiteralMethod writeMethod true (lambdaDepth + 1)
                    block_stmts.Flush()
                    stmtCollArg.Add (CodeSnippetStatement (sprintf "this.DefineSection(\"%s\", (%s%d) => {\r\n\t %s \r\n\t});" name textWriterName (lambdaDepth + 1) (buf.ToString()) ))

            | KeywordConditionalBlock (keyname, cond, block) -> // of string * ASTNode * ASTNode
                let conditionCode = fold_params_into_buf cond
                let blockCode = fold_into_buf block
                stmtCollArg.Add (CodeSnippetStatement (sprintf "%s %s \r\n{ %s }" keyname conditionCode blockCode))

            | ImportNamespaceDirective ns ->
                rootNs.Imports.Add (CodeNamespaceImport ns) |> ignore

            | FunctionsBlock code ->
                typeDecl.Members.Add (CodeSnippetTypeMember(code)) |> ignore

            | InheritDirective baseClass -> // of string
                typeDecl.BaseTypes.Clear()
                typeDecl.BaseTypes.Add (CodeTypeReference(baseClass)) |> ignore

            | ModelDirective modelType -> // of string
                let baseType = typeDecl.BaseTypes.[0]
                baseType.TypeArguments.Clear()
                baseType.TypeArguments.Add (CodeTypeReference(modelType)) |> ignore

            | HelperDecl (name, args, block) -> // of string * ASTNode * ASTNode
                // public Castle.Blade.Web.HtmlResult testing () 
                // {
                //     return new Castle.Blade.Web.HtmlResult(_writer => {
                //         WriteLiteralTo(_writer, "    <b>test test test</b>\r\n");
                //     });
                // }
                
                (*
                let buf = StringBuilder()
                match args with 
                | Code c ->
                    buf.Append "(" |> ignore
                    buf.Append c  |> ignore
                    buf.Append ")" |> ignore
                | Comment -> ()
                | _ -> failwithf "Unexpected node in helper declaration %O" args
                
                let blockbuf = StringBuilder()
                let block_stmts = StmtCollWrapper.FromBuffer blockbuf
                gen_code block rootNs typeDecl compUnit block_stmts writeLiteralMethod writeMethod true (lambdaDepth + 1)
                block_stmts.Flush()
                *)

                let conditionCode = fold_params_into_buf args
                let blockCode = fold_into_buf block

                typeDecl.Members.Add 
                    (CodeSnippetTypeMember(
                        sprintf "public HtmlResult %s %O { \r\n\treturn new HtmlResult(%s%d => { \r\n%O }); }" name conditionCode textWriterName (lambdaDepth + 1) blockCode  )) |> ignore

            | TryStmt (block,catches,final) -> // of ASTNode * ASTNode list option * ASTNode option 
                ()

            | DoWhileStmt (block, cond) -> // of ASTNode * ASTNode
                ()

            | _ -> () // Comment / None 


        and GenerateCodeFromAST (typeName:string) (node:ASTNode) (options:CodeGenOptions) = 
            let typeDecl = CodeTypeDeclaration(typeName, IsClass=true)
            let compileUnit, rootNs = build_compilation_unit typeDecl options

            let _execMethod = CodeMemberMethod( Name = options.RenderMethodName, Attributes = (MemberAttributes.Override ||| MemberAttributes.Public) )
            let writeLiteralMethod = CodeMethodReferenceExpression(null, options.WriteLiteralMethodName)
            let writeMethod = CodeMethodReferenceExpression(null, options.WriteMethodName)
            
            typeDecl.Members.Add _execMethod |> ignore

            let stmts = StmtCollWrapper.FromMethod _execMethod
            gen_code node rootNs typeDecl compileUnit stmts writeLiteralMethod writeMethod false 0
            stmts.Flush()
            compileUnit



