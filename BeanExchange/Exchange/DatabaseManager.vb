Imports System.IO
Imports Microsoft.Data.Sqlite

Public Class DatabaseManager
    Private ReadOnly _connectionString As String
    Private ReadOnly _dbFilePath As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "exchange.db")

    Public Sub New(connectionString As String)
        _connectionString = connectionString
    End Sub


    ' New method to update (or insert) a token price
    Public Sub UpdateTokenPrice(tokenSymbol As String, price As Decimal, timestamp As Long)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("INSERT OR REPLACE INTO TokenPrices (TokenSymbol, Price, Timestamp) VALUES (@TokenSymbol, @Price, @Timestamp)", connection)
                command.Parameters.AddWithValue("@TokenSymbol", tokenSymbol)
                command.Parameters.AddWithValue("@Price", price)
                command.Parameters.AddWithValue("@Timestamp", timestamp)
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    'Gets token prices from the database
    Public Function GetTokenPricesDB(tokenSymbol As String, Optional fromTimestamp As Long = 0, Optional toTimestamp As Long = Long.MaxValue) As List(Of TokenPrice)
        Dim prices As New List(Of TokenPrice)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Dim commandText As String = "SELECT TokenPriceId, TokenSymbol, Price, Timestamp FROM TokenPrices WHERE TokenSymbol = @TokenSymbol "

            If fromTimestamp <> 0 Then
                commandText &= "AND Timestamp >= @FromTimestamp "
            End If

            If toTimestamp <> Long.MaxValue Then
                commandText &= "AND Timestamp <= @ToTimestamp "
            End If

            commandText &= "ORDER BY Timestamp"

            Using command As New SqliteCommand(commandText, connection)
                command.Parameters.AddWithValue("@TokenSymbol", tokenSymbol)
                If fromTimestamp <> 0 Then
                    command.Parameters.AddWithValue("@FromTimestamp", fromTimestamp)
                End If
                If toTimestamp <> Long.MaxValue Then
                    command.Parameters.AddWithValue("@ToTimestamp", toTimestamp)
                End If

                Using reader As SqliteDataReader = command.ExecuteReader()
                    While reader.Read()
                        Dim price As New TokenPrice() With {
                            .TokenPriceId = reader.GetInt32(0),
                            .TokenSymbol = reader.GetString(1),
                            .Price = reader.GetDecimal(2),
                            .Timestamp = reader.GetInt64(3)
                        }
                        prices.Add(price)
                    End While
                End Using
            End Using
        End Using
        Return prices
    End Function

    ' New method to get token prices from the database
    Public Function GetTokenPrices(tokenSymbol As String, fromTimestamp As Long, toTimestamp As Long) As List(Of TokenPrice)
        Return GetTokenPricesDB(tokenSymbol, fromTimestamp, toTimestamp)
    End Function

    Public Function GetLatestTokenPrice(tokenSymbol As String) As List(Of TokenPrice)
        Return GetTokenPricesDB(tokenSymbol).OrderByDescending(Function(p) p.Timestamp).Take(1).ToList()
    End Function

    Public Function GetUserBalance(userId As Integer, asset As String) As Decimal
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT Balance FROM Balances WHERE UserId = @UserId AND Asset = @Asset", connection)
                command.Parameters.AddWithValue("@UserId", userId)
                command.Parameters.AddWithValue("@Asset", asset)
                Dim result As Object = command.ExecuteScalar()
                If result IsNot DBNull.Value AndAlso result IsNot Nothing Then
                    Return CDec(result)
                Else
                    Return 0
                End If
            End Using
        End Using
    End Function

    Public Function UserExists(username As String) As Boolean
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT COUNT(*) FROM Users WHERE Username = @Username", connection)
                command.Parameters.AddWithValue("@Username", username)
                Dim count As Long = CLng(command.ExecuteScalar())
                Return count > 0
            End Using
        End Using
    End Function

    Public Function CreateUser(registrationData As RegistrationData) As Integer
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("INSERT INTO Users (Username, PasswordHash) VALUES (@Username, @PasswordHash); SELECT last_insert_rowid();", connection)
                command.Parameters.AddWithValue("@Username", registrationData.Username)
                command.Parameters.AddWithValue("@PasswordHash", WalletHandler.HashPassword(registrationData.Password))
                Return CInt(command.ExecuteScalar())
            End Using
        End Using
    End Function

    Public Sub StoreApiKey(userId As Integer, apiKey As String)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("INSERT OR REPLACE INTO ApiKeys (UserId, ApiKey) VALUES (@UserId, @ApiKey)", connection)
                command.Parameters.AddWithValue("@UserId", userId)
                command.Parameters.AddWithValue("@ApiKey", apiKey)
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Public Function GetApiKey(userId As Integer) As String
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT ApiKey FROM ApiKeys WHERE UserId = @UserId", connection)
                command.Parameters.AddWithValue("@UserId", userId)
                Dim apiKey As Object = command.ExecuteScalar()
                If apiKey IsNot DBNull.Value AndAlso apiKey IsNot Nothing Then
                    Return apiKey.ToString()
                Else
                    Return String.Empty
                End If
            End Using
        End Using
    End Function

    Public Sub UpdateBalance(userId As Integer, asset As String, balance As Decimal)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using transaction As SqliteTransaction = connection.BeginTransaction()
                Try
                    UpdateBalanceInDatabase(userId, asset, balance, connection, transaction)
                    transaction.Commit()
                Catch ex As Exception
                    transaction.Rollback()
                    Console.WriteLine($"Error updating user balance: {ex.Message}")
                End Try
            End Using
        End Using
    End Sub

    Private Sub UpdateBalanceInDatabase(userId As Integer, asset As String, balance As Decimal, connection As SqliteConnection, transaction As SqliteTransaction)
        Using command As New SqliteCommand("UPDATE Balances SET Balance = @Balance WHERE UserId = @UserId AND Asset = @Asset", connection, transaction)
            command.Parameters.AddWithValue("@Balance", balance)
            command.Parameters.AddWithValue("@UserId", userId)
            command.Parameters.AddWithValue("@Asset", asset)
            Dim rowsAffected As Integer = command.ExecuteNonQuery()

            If rowsAffected = 0 Then
                Using insertCommand As New SqliteCommand("INSERT INTO Balances (UserId, Asset, Balance) VALUES (@UserId, @Asset, @Balance)", connection, transaction)
                    insertCommand.Parameters.AddWithValue("@UserId", userId)
                    insertCommand.Parameters.AddWithValue("@Asset", asset)
                    insertCommand.Parameters.AddWithValue("@Balance", balance)
                    insertCommand.ExecuteNonQuery()
                End Using
            End If
        End Using
    End Sub

    Public Function GetEncryptedPrivateKey(depositAddress As String) As String
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT PrivateKey FROM DepositAddresses WHERE DepositAddress = @DepositAddress", connection)
                command.Parameters.AddWithValue("@DepositAddress", depositAddress)
                Dim result As Object = command.ExecuteScalar()
                If result IsNot DBNull.Value AndAlso result IsNot Nothing Then
                    Return result.ToString()
                Else
                    Return String.Empty
                End If
            End Using
        End Using
    End Function

    Public Function GetPrivateKeyIV(depositAddress As String) As String
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT IV FROM DepositAddresses WHERE DepositAddress = @DepositAddress", connection)
                command.Parameters.AddWithValue("@DepositAddress", depositAddress)
                Dim result As Object = command.ExecuteScalar()
                If result IsNot DBNull.Value AndAlso result IsNot Nothing Then
                    Return result.ToString()
                Else
                    Return String.Empty
                End If
            End Using
        End Using
    End Function

    Public Function GetExchangePrivateKey() As String
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            ' Explicitly select the private key for the "Users funds wallet"
            Using command As New SqliteCommand("SELECT PrivateKey, IV FROM ExchangeWallet WHERE WalletName = 'Users funds wallet'", connection)
                Using reader As SqliteDataReader = command.ExecuteReader()
                    If reader.Read() Then
                        Dim encryptedPrivateKey As String = reader.GetString(0)
                        Dim iv As String = reader.GetString(1)
                        Return WalletHandler.DecryptPrivateKey(encryptedPrivateKey, iv, "112233")
                    Else
                        Return String.Empty
                    End If
                End Using
            End Using
        End Using
    End Function

    Public Sub StoreDepositAddress(userId As Integer, depositAddress As String, privateKey As String, iv As String)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("INSERT INTO DepositAddresses (UserId, DepositAddress, PrivateKey, IV) VALUES (@UserId, @DepositAddress, @PrivateKey, @IV)", connection)
                command.Parameters.AddWithValue("@UserId", userId)
                command.Parameters.AddWithValue("@DepositAddress", depositAddress)
                command.Parameters.AddWithValue("@PrivateKey", privateKey)
                command.Parameters.AddWithValue("@IV", iv)
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Public Function GetDepositAddress(userId As Integer) As String
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT DepositAddress FROM DepositAddresses WHERE UserId = @UserId", connection)
                command.Parameters.AddWithValue("@UserId", userId)
                Dim address As Object = command.ExecuteScalar()
                If address IsNot DBNull.Value AndAlso address IsNot Nothing Then
                    Return address.ToString()
                Else
                    Return String.Empty
                End If
            End Using
        End Using
    End Function

    Public Function GetAllDepositAddresses() As List(Of String)
        Dim depositAddresses As New List(Of String)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT DepositAddress FROM DepositAddresses", connection)
                Using reader As SqliteDataReader = command.ExecuteReader()
                    While reader.Read()
                        depositAddresses.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using
        Return depositAddresses
    End Function

    Public Function GetUserIdFromDepositAddress(depositAddress As String) As Integer
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT UserId FROM DepositAddresses WHERE DepositAddress = @DepositAddress", connection)
                command.Parameters.AddWithValue("@DepositAddress", depositAddress)
                Dim result As Object = command.ExecuteScalar()
                If result IsNot DBNull.Value AndAlso result IsNot Nothing Then
                    Return CInt(result)
                Else
                    Return -1 ' Or handle the case where the address is not found
                End If
            End Using
        End Using
    End Function

    Public Function GetExchangeWalletAddress() As String
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT DepositAddress FROM ExchangeWallet WHERE WalletName = 'Users funds wallet'", connection)
                Dim result As Object = command.ExecuteScalar()
                If result IsNot DBNull.Value AndAlso result IsNot Nothing Then
                    Return result.ToString()
                Else
                    Return String.Empty ' Or handle the case where the address is not found
                End If
            End Using
        End Using
    End Function

    Public Function GetPublicKey(userId As Integer) As String
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT DepositAddress FROM DepositAddresses WHERE UserId = @UserId", connection)
                command.Parameters.AddWithValue("@UserId", userId)
                Dim result As Object = command.ExecuteScalar()
                If result IsNot DBNull.Value AndAlso result IsNot Nothing Then
                    Dim publicKey As String = result.ToString()
                    Return publicKey
                Else
                    Return String.Empty
                End If
            End Using
        End Using
    End Function

    Public Sub CreateDatabaseIfNotExist()
        If Not File.Exists(_dbFilePath) Then
            ' Create the database file using Microsoft.Data.Sqlite
            Using connection As New Microsoft.Data.Sqlite.SqliteConnection(_connectionString)
                connection.Open() ' This will create the database file if it doesn't exist
            End Using

            ' Now that the file exists, create the tables
            Using connection As New Microsoft.Data.Sqlite.SqliteConnection(_connectionString)
                connection.Open()
                ' Corrected: Pass the connection object to the SqliteCommand constructor
                Using command As New Microsoft.Data.Sqlite.SqliteCommand(Nothing) ' Initialize with Nothing
                    command.Connection = connection ' Set the Connection property
                    ' Create Users table
                    command.CommandText = "CREATE TABLE Users (UserId INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT UNIQUE, PasswordHash TEXT)"
                    command.ExecuteNonQuery()

                    ' Create ApiKeys table
                    command.CommandText = "CREATE TABLE ApiKeys (ApiKeyId INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER, ApiKey TEXT UNIQUE, FOREIGN KEY (UserId) REFERENCES Users(UserId))"
                    command.ExecuteNonQuery()

                    ' Create Balances table
                    command.CommandText = "CREATE TABLE Balances (BalanceId INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER, Asset TEXT, Balance REAL, FOREIGN KEY (UserId) REFERENCES Users(UserId))"
                    command.ExecuteNonQuery()

                    ' Create DepositAddresses table
                    command.CommandText = "CREATE TABLE DepositAddresses (DepositAddressId INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER UNIQUE, DepositAddress TEXT UNIQUE, PrivateKey TEXT, IV TEXT, FOREIGN KEY (UserId) REFERENCES Users(UserId))"  ' Include IV column here
                    command.ExecuteNonQuery()

                    ' Create ExchangeWallet table
                    command.CommandText = "CREATE TABLE ExchangeWallet (ExchangeWalletId INTEGER PRIMARY KEY AUTOINCREMENT, WalletName TEXT, DepositAddress TEXT UNIQUE, PrivateKey TEXT, IV TEXT)"
                    command.ExecuteNonQuery()

                    ' Create TokenPrices table
                    command.CommandText = "CREATE TABLE TokenPrices (TokenPriceId INTEGER PRIMARY KEY AUTOINCREMENT, TokenSymbol TEXT, Price REAL, Timestamp INTEGER)"
                    command.ExecuteNonQuery()

                    ' Create Orders table
                    command.CommandText = "CREATE TABLE Orders (OrderId TEXT PRIMARY KEY, UserId INTEGER, TokenSymbol TEXT, IsBuyOrder INTEGER, Quantity REAL, Price REAL, Timestamp INTEGER)"
                    command.ExecuteNonQuery()

                    ' Create Trades table <-- ADD THIS
                    command.CommandText = "CREATE TABLE Trades (TradeId TEXT PRIMARY KEY, BuyerUserId INTEGER, SellerUserId INTEGER, TokenSymbol TEXT, Quantity REAL, Price REAL, Timestamp INTEGER)"
                    command.ExecuteNonQuery()
                End Using

                ' Generate the exchange wallet
                GenerateExchangeWallet(connection)
            End Using
        End If
    End Sub
    ' --- Add these new methods to DatabaseManager ---

    Public Sub AddOrder(order As Order)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("INSERT INTO Orders (OrderId, UserId, TokenSymbol, IsBuyOrder, Quantity, Price, Timestamp) VALUES (@OrderId, @UserId, @TokenSymbol, @IsBuyOrder, @Quantity, @Price, @Timestamp)", connection)
                command.Parameters.AddWithValue("@OrderId", order.OrderId.ToString()) ' Store GUID as TEXT
                command.Parameters.AddWithValue("@UserId", order.UserId)
                command.Parameters.AddWithValue("@TokenSymbol", order.TokenSymbol)
                command.Parameters.AddWithValue("@IsBuyOrder", If(order.IsBuyOrder, 1, 0)) ' Convert Boolean to Integer
                command.Parameters.AddWithValue("@Quantity", order.Quantity)
                command.Parameters.AddWithValue("@Price", order.Price)
                command.Parameters.AddWithValue("@Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds()) 'Use UTC
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Public Sub UpdateOrder(order As Order)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("UPDATE Orders SET Quantity = @Quantity WHERE OrderId = @OrderId", connection)
                command.Parameters.AddWithValue("@Quantity", order.Quantity)
                command.Parameters.AddWithValue("@OrderId", order.OrderId.ToString())
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Public Sub AddTrade(buyOrder As Order, sellOrder As Order, quantity As Decimal, price As Decimal)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("INSERT INTO Trades (TradeId, BuyerUserId, SellerUserId, TokenSymbol, Quantity, Price, Timestamp) VALUES (@TradeId, @BuyerUserId, @SellerUserId, @TokenSymbol, @Quantity, @Price, @Timestamp)", connection)
                command.Parameters.AddWithValue("@TradeId", Guid.NewGuid().ToString())
                command.Parameters.AddWithValue("@BuyerUserId", buyOrder.UserId)
                command.Parameters.AddWithValue("@SellerUserId", sellOrder.UserId)
                command.Parameters.AddWithValue("@TokenSymbol", buyOrder.TokenSymbol)
                command.Parameters.AddWithValue("@Quantity", quantity)
                command.Parameters.AddWithValue("@Price", price)
                command.Parameters.AddWithValue("@Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Public Sub DeleteOrder(orderId As Guid)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("DELETE FROM Orders WHERE OrderId = @OrderId", connection)
                command.Parameters.AddWithValue("@OrderId", orderId.ToString())
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Public Function GetOrders(Optional tokenSymbol As String = Nothing, Optional isBuyOrder As Boolean? = Nothing) As List(Of Order)
        Dim orders As New List(Of Order)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Dim commandText As String = "SELECT OrderId, UserId, TokenSymbol, IsBuyOrder, Quantity, Price FROM Orders"

            ' Build the WHERE clause conditionally
            Dim whereClauses As New List(Of String)
            If Not String.IsNullOrEmpty(tokenSymbol) Then
                whereClauses.Add("TokenSymbol = @TokenSymbol")
            End If
            If isBuyOrder.HasValue Then
                whereClauses.Add("IsBuyOrder = @IsBuyOrder")
            End If

            If whereClauses.Count > 0 Then
                commandText += " WHERE " + String.Join(" AND ", whereClauses)
            End If

            commandText += " ORDER BY Price, OrderId" ' Basic ordering

            Using command As New SqliteCommand(commandText, connection)
                ' Add parameters conditionally
                If Not String.IsNullOrEmpty(tokenSymbol) Then
                    command.Parameters.AddWithValue("@TokenSymbol", tokenSymbol)
                End If
                If isBuyOrder.HasValue Then
                    command.Parameters.AddWithValue("@IsBuyOrder", If(isBuyOrder.Value, 1, 0)) ' Convert Boolean to Integer
                End If

                Using reader As SqliteDataReader = command.ExecuteReader()
                    While reader.Read()
                        ' --- NULL CHECKS (from previous response) ---
                        If reader.IsDBNull(1) OrElse reader.IsDBNull(2) OrElse reader.IsDBNull(4) OrElse reader.IsDBNull(5) Then
                            Console.WriteLine("Warning: Skipping order with NULL values in required columns.")
                            Continue While ' Skip to the next row
                        End If

                        Dim order As New Order(
                        reader.GetInt32(1), ' UserId
                        reader.GetString(2), ' TokenSymbol
                        If(reader.GetInt32(3) = 1, True, False), ' IsBuyOrder
                        reader.GetDecimal(4), ' Quantity
                        reader.GetDecimal(5)  ' Price
                    ) With {.OrderId = Guid.Parse(reader.GetString(0))} ' Parse the OrderId
                        orders.Add(order)
                    End While
                End Using
            End Using
        End Using
        Return orders
    End Function

    Public Sub UpdateLastTradedPrice(tokenSymbol As String, price As Decimal)
        Dim currentTimeStamp As Long = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        UpdateTokenPrice(tokenSymbol, price, currentTimeStamp)
    End Sub

    Private Sub GenerateExchangeWallet(connection As SqliteConnection)

        ' Generate "Users funds wallet"
        GenerateAndStoreWallet(connection, "Users funds wallet")

        ' Generate "Reserve wallet"
        GenerateAndStoreWallet(connection, "Reserve wallet")

    End Sub

    Private Sub GenerateAndStoreWallet(connection As SqliteConnection, walletName As String)
        Dim wallet As Tuple(Of String, String) = WalletHandler.GenerateWallet()
        Dim publicKey As String = wallet.Item1
        Dim privateKey As String = wallet.Item2

        Dim encryptedKeyAndIV As Tuple(Of String, String) = WalletHandler.EncryptPrivateKey(privateKey, "112233")
        Dim encryptedPrivateKey As String = encryptedKeyAndIV.Item1
        Dim iv As String = encryptedKeyAndIV.Item2

        Using command As New Microsoft.Data.Sqlite.SqliteCommand("INSERT INTO ExchangeWallet (WalletName, DepositAddress, PrivateKey, IV) VALUES (@WalletName, @DepositAddress, @PrivateKey, @IV)", connection)
            command.Parameters.AddWithValue("@WalletName", walletName)
            command.Parameters.AddWithValue("@DepositAddress", publicKey)
            command.Parameters.AddWithValue("@PrivateKey", encryptedPrivateKey)
            command.Parameters.AddWithValue("@IV", iv)
            command.ExecuteNonQuery()
        End Using

        Console.WriteLine("-------------------------------")
        Console.WriteLine($"{walletName}:")
        Console.WriteLine($"Public key: {publicKey}")
        Console.WriteLine($"Private key: {privateKey}")
        Console.WriteLine("-------------------------------")
    End Sub

    Public Function GetAllTokenPrices(fromTimestamp As Long, toTimestamp As Long) As List(Of TokenPrice)
        Dim prices As New List(Of TokenPrice)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            ' Notice:  NO WHERE clause for tokenSymbol
            Using command As New SqliteCommand("SELECT TokenPriceId, TokenSymbol, Price, Timestamp FROM TokenPrices WHERE Timestamp >= @FromTimestamp AND Timestamp <= @ToTimestamp ORDER BY Timestamp", connection)
                command.Parameters.AddWithValue("@FromTimestamp", fromTimestamp)
                command.Parameters.AddWithValue("@ToTimestamp", toTimestamp)
                Using reader As SqliteDataReader = command.ExecuteReader()
                    While reader.Read()
                        Dim price As New TokenPrice() With {
                            .TokenPriceId = reader.GetInt32(0),
                            .TokenSymbol = reader.GetString(1),
                            .Price = reader.GetDecimal(2),
                            .Timestamp = reader.GetInt64(3)
                        }
                        prices.Add(price)
                    End While
                End Using
            End Using
        End Using
        Return prices
    End Function
End Class