//
// Mono.Data.SybaseClient.SybaseCommand.cs
//
// Author:
//   Rodrigo Moya (rodrigo@ximian.com)
//   Daniel Morgan (danmorg@sc.rr.com)
//   Tim Coleman (tim@timcoleman.com)
//
// (C) Ximian, Inc 2002 http://www.ximian.com/
// (C) Daniel Morgan, 2002
// Copyright (C) Tim Coleman, 2002
//

using Mono.Data.Tds;
using Mono.Data.Tds.Protocol;
using System;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Runtime.InteropServices;
using System.Text;

namespace Mono.Data.SybaseClient {
	public sealed class SybaseCommand : Component, IDbCommand, ICloneable
	{
		#region Fields

		bool disposed = false;
		int commandTimeout;
		bool designTimeVisible;
		string commandText;
		CommandType commandType;
		SybaseConnection connection;
		SybaseTransaction transaction;
		UpdateRowSource updatedRowSource;
		CommandBehavior behavior = CommandBehavior.Default;
		SybaseParameterCollection parameters;
		string preparedStatement = null;

		#endregion // Fields

		#region Constructors

		public SybaseCommand() 
			: this (String.Empty, null, null)
		{
		}

		public SybaseCommand (string commandText) 
			: this (commandText, null, null)
		{
			commandText = commandText;
		}

		public SybaseCommand (string commandText, SybaseConnection connection) 
			: this (commandText, connection, null)
		{
			Connection = connection;
		}

		public SybaseCommand (string commandText, SybaseConnection connection, SybaseTransaction transaction) 
		{
			this.commandText = commandText;
			this.connection = connection;
			this.transaction = transaction;
			this.commandType = CommandType.Text;
			this.updatedRowSource = UpdateRowSource.Both;

			this.designTimeVisible = false;
			this.commandTimeout = 30;
			parameters = new SybaseParameterCollection (this);
		}

		#endregion // Constructors

		#region Properties

		internal CommandBehavior CommandBehavior {
			get { return behavior; }
		}

		public string CommandText {
			get { return commandText; }
			set { 
				if (value != commandText && preparedStatement != null)
					Unprepare ();
				commandText = value; 
			}
		}

		public int CommandTimeout {
			get { return commandTimeout;  }
			set { 
				if (commandTimeout < 0)
					throw new ArgumentException ("The property value assigned is less than 0.");
				commandTimeout = value; 
			}
		}

		public CommandType CommandType	{
			get { return commandType; }
			set { 
				if (value == CommandType.TableDirect)
					throw new ArgumentException ("CommandType.TableDirect is not supported by the Mono SybaseClient Data Provider.");
				commandType = value; 
			}
		}

		public SybaseConnection Connection {
			get { return connection; }
			set { 
				if (transaction != null && connection.Transaction != null && connection.Transaction.IsOpen)
					throw new InvalidOperationException ("The Connection property was changed while a transaction was in progress.");
				transaction = null;
				connection = value; 
			}
		}

		public bool DesignTimeVisible {
			get { return designTimeVisible; } 
			set { designTimeVisible = value; }
		}

		public SybaseParameterCollection Parameters {
			get { return parameters; }
		}

		internal ITds Tds {
			get { return Connection.Tds; }
		}

		IDbConnection IDbCommand.Connection {
			get { return Connection; }
			set { 
				if (!(value is SybaseConnection))
					throw new InvalidCastException ("The value was not a valid SybaseConnection.");
				Connection = (SybaseConnection) value;
			}
		}

		IDataParameterCollection IDbCommand.Parameters	{
			get { return Parameters; }
		}

		IDbTransaction IDbCommand.Transaction {
			get { return Transaction; }
			set { 
				if (!(value is SybaseTransaction))
					throw new ArgumentException ();
				Transaction = (SybaseTransaction) value; 
			}
		}

		public SybaseTransaction Transaction {
			get { return transaction; }
			set { transaction = value; }
		}	

		public UpdateRowSource UpdatedRowSource	{
			get { return updatedRowSource; }
			set { updatedRowSource = value; }
		}

		#endregion // Fields

		#region Methods

		public void Cancel () 
		{
			if (Connection == null || Connection.Tds == null)
				return;
			Connection.Tds.Cancel ();
		}

		internal void CloseDataReader (bool moreResults)
		{
			GetOutputParameters ();
			Connection.DataReader = null;

			if ((behavior & CommandBehavior.CloseConnection) != 0)
				Connection.Close ();
		}

		public SybaseParameter CreateParameter () 
		{
			return new SybaseParameter ();
		}

		internal void DeriveParameters ()
		{
			if (commandType != CommandType.StoredProcedure)
				throw new InvalidOperationException (String.Format ("SybaseCommand DeriveParameters only supports CommandType.StoredProcedure, not CommandType.{0}", commandType));
			ValidateCommand ("DeriveParameters");

			SybaseParameterCollection localParameters = new SybaseParameterCollection (this);
			localParameters.Add ("@P1", SybaseType.NVarChar, commandText.Length).Value = commandText;

			string sql = "sp_procedure_params_rowset";

			Connection.Tds.ExecProc (sql, localParameters.MetaParameters, 0, true);

			SybaseDataReader reader = new SybaseDataReader (this);
			parameters.Clear ();
			object[] dbValues = new object[reader.FieldCount];

			while (reader.Read ()) {
				reader.GetValues (dbValues);
				parameters.Add (new SybaseParameter (dbValues));
			}
			reader.Close ();	
		}

		private void Execute (CommandBehavior behavior, bool wantResults)
		{
			TdsMetaParameterCollection parms = Parameters.MetaParameters;
			if (preparedStatement == null) {
				bool schemaOnly = ((CommandBehavior & CommandBehavior.SchemaOnly) > 0);
				bool keyInfo = ((CommandBehavior & CommandBehavior.SchemaOnly) > 0);

				StringBuilder sql1 = new StringBuilder ();
				StringBuilder sql2 = new StringBuilder ();

				if (schemaOnly || keyInfo)
					sql1.Append ("SET FMTONLY OFF;");
				if (keyInfo) {
					sql1.Append ("SET NO_BROWSETABLE ON;");
					sql2.Append ("SET NO_BROWSETABLE OFF;");
				}
				if (schemaOnly) {
					sql1.Append ("SET FMTONLY ON;");
					sql2.Append ("SET FMTONLY OFF;");
				}

				switch (CommandType) {
				case CommandType.StoredProcedure:
					if (keyInfo || schemaOnly)
						Connection.Tds.Execute (sql1.ToString ());
					Connection.Tds.ExecProc (CommandText, parms, CommandTimeout, wantResults);
					if (keyInfo || schemaOnly)
						Connection.Tds.Execute (sql2.ToString ());
					break;
				case CommandType.Text:
					string sql = String.Format ("{0}{1}{2}", sql1.ToString (), CommandText, sql2.ToString ());
					Connection.Tds.Execute (sql, parms, CommandTimeout, wantResults);
					break;
				}
			}
			else 
				Connection.Tds.ExecPrepared (preparedStatement, parms, CommandTimeout, wantResults);
		}

		public int ExecuteNonQuery ()
		{
			ValidateCommand ("ExecuteNonQuery");
			int result = 0;

			try {
				Execute (CommandBehavior.Default, false);
			}
			catch (TdsTimeoutException e) {
				throw SybaseException.FromTdsInternalException ((TdsInternalException) e);
			}

			GetOutputParameters ();
			return result;
		}

		public SybaseDataReader ExecuteReader ()
		{
			return ExecuteReader (CommandBehavior.Default);
		}

		public SybaseDataReader ExecuteReader (CommandBehavior behavior)
		{
			ValidateCommand ("ExecuteReader");
			try {
				Execute (behavior, true);
			}
			catch (TdsTimeoutException e) {
				throw SybaseException.FromTdsInternalException ((TdsInternalException) e);
			}
			Connection.DataReader = new SybaseDataReader (this);
			return Connection.DataReader;
		}

		public object ExecuteScalar ()
		{
			ValidateCommand ("ExecuteScalar");
			try {
				Execute (CommandBehavior.Default, true);
			}
			catch (TdsTimeoutException e) {
				throw SybaseException.FromTdsInternalException ((TdsInternalException) e);
			}

			if (!Connection.Tds.NextResult () || !Connection.Tds.NextRow ())
				return null;

			object result = Connection.Tds.ColumnValues [0];
			CloseDataReader (true);
			return result;
		}

		private void GetOutputParameters ()
		{
			Connection.Tds.SkipToEnd ();

			IList list = Connection.Tds.ColumnValues;

			if (list != null && list.Count > 0) {
				int index = 0;
				foreach (SybaseParameter parameter in parameters) {
					if (parameter.Direction != ParameterDirection.Input) {
						parameter.Value = list [index];
						index += 1;
					}
					if (index >= list.Count)
						break;
				}
			}
		}

		object ICloneable.Clone ()
		{
			return new SybaseCommand (commandText, Connection);
		}

		IDbDataParameter IDbCommand.CreateParameter ()
		{
			return CreateParameter ();
		}

		IDataReader IDbCommand.ExecuteReader ()
		{
			return ExecuteReader ();
		}

		IDataReader IDbCommand.ExecuteReader (CommandBehavior behavior)
		{
			return ExecuteReader (behavior);
		}

		public void Prepare ()
		{
			ValidateCommand ("Prepare");
			if (CommandType == CommandType.Text) 
				preparedStatement = Connection.Tds.Prepare (CommandText, Parameters.MetaParameters);
		}

		public void ResetCommandTimeout ()
		{
			commandTimeout = 30;
		}

		private void Unprepare ()
		{
			Connection.Tds.Unprepare (preparedStatement); 
			preparedStatement = null;
		}

		private void ValidateCommand (string method)
		{
			if (Connection == null)
				throw new InvalidOperationException (String.Format ("{0} requires a Connection object to continue.", method));
			if (Connection.Transaction != null && transaction != Connection.Transaction)
				throw new InvalidOperationException ("The Connection object does not have the same transaction as the command object.");
			if (Connection.State != ConnectionState.Open)
				throw new InvalidOperationException (String.Format ("ExecuteNonQuery requires an open Connection object to continue. This connection is closed.", method));
			if (commandText == String.Empty || commandText == null)
				throw new InvalidOperationException ("The command text for this Command has not been set.");
			if (Connection.DataReader != null)
				throw new InvalidOperationException ("There is already an open DataReader associated with this Connection which must be closed first.");
		}

		#endregion // Methods
	}
}
