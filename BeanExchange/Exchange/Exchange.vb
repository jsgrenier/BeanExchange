Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Net
Imports Newtonsoft.Json.Linq
Imports Microsoft.Data.Sqlite
Imports System.Security.Cryptography

Public Class Exchange
    Implements IDisposable

    Public ReadOnly _apiKeys As Dictionary(Of String, Integer) = New Dictionary(Of String, Integer)
    Private ReadOnly _connectionString As String = "Data Source=exchange.db;"
    Private ReadOnly _databaseManager As DatabaseManager
    Private ReadOnly _apiClient As APIClient

    Private _monitoringTask As Task
    Private _cts As CancellationTokenSource

    ' Add a task and cancellation token for price monitoring
    Private _priceMonitoringTask As Task
    Private _priceCts As CancellationTokenSource

    'The order book, a list for buy and sell order, both ordered by price.
    Private ReadOnly _buyOrders As List(Of Order)
    Private ReadOnly _sellOrders As List(Of Order)

    Public Sub New(databaseManager As DatabaseManager, apiClient As APIClient)
        _databaseManager = databaseManager
        _apiClient = apiClient

        _databaseManager.CreateDatabaseIfNotExist()
        LoadApiKeys()
        _cts = New CancellationTokenSource()
        StartMonitoring()

        ' Start price monitoring
        _priceCts = New CancellationTokenSource()
        StartPriceMonitoring()

        'Initialize the order book lists
        _buyOrders = New List(Of Order)()
        _sellOrders = New List(Of Order)()
        LoadOrders() ' Load orders from the database
    End Sub
    ' --- End of Order Book Data Structures ---

    Private Sub StartMonitoring()
        _monitoringTask = Task.Run(Sub()
                                       While Not _cts.IsCancellationRequested
                                           MonitorBalances()
                                           Task.Delay(TimeSpan.FromSeconds(10)).Wait()
                                       End While
                                   End Sub)
    End Sub

    ' Modified method to start price monitoring with a 60-second interval
    Private Sub StartPriceMonitoring()
        _priceMonitoringTask = Task.Run(Sub()
                                            While Not _priceCts.IsCancellationRequested
                                                UpdateTokenPrices()
                                                Task.Delay(TimeSpan.FromSeconds(60)).Wait() ' Changed to 60 seconds
                                            End While
                                        End Sub)
    End Sub

    'Update the token prices logic
    Private Sub UpdateTokenPrices()
        Try
            Dim tokenNames As Dictionary(Of String, String) = GetTokenNames()
            Dim currentTimeStamp As Long = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

            For Each tokenSymbol In tokenNames.Keys
                'Get price from latest token price
                Dim price As Decimal = _databaseManager.GetLatestTokenPrice(tokenSymbol).FirstOrDefault()?.Price
                _databaseManager.UpdateTokenPrice(tokenSymbol, price, currentTimeStamp)
            Next

        Catch ex As Exception
            Console.WriteLine($"Error updating token prices: {ex.Message}")
            ' Consider logging the exception to a file or error tracking system.
        End Try
    End Sub



    Private Sub MonitorBalances()
        Dim depositAddresses As List(Of String) = _databaseManager.GetAllDepositAddresses()
        Dim tokenNames As Dictionary(Of String, String) = GetTokenNames()
        For Each address In depositAddresses
            For Each tokenSymbol In tokenNames.Keys
                HandleDepositAddress(address, tokenSymbol)
            Next
        Next
    End Sub
    Private Sub HandleDepositAddress(address As String, tokenSymbol As String)
        Dim balance As Decimal = GetBalanceForAddress(address, tokenSymbol)
        If balance > 0 Then
            TransferTokensToExchange(address, tokenSymbol, balance)
        End If
    End Sub
    Private Function GetBalanceForAddress(address As String, tokenSymbol As String) As Decimal
        Dim encodedPublicKey As String = WebUtility.UrlEncode(address)
        Dim jsonObject As JObject = _apiClient.GettAsync("/get_tokens_owned", New Dictionary(Of String, String) From {{"address", encodedPublicKey}}).Result
        Return GetBalanceFromBlockchainResponse(jsonObject, tokenSymbol)
    End Function

    Private Sub TransferTokensToExchange(address As String, tokenSymbol As String, balance As Decimal)
        Dim userId As Integer = _databaseManager.GetUserIdFromDepositAddress(address)
        Dim exchangeLedgerAddress As String = _databaseManager.GetExchangeWalletAddress()
        Dim privateKey As String = GetPrivateKeyForAddress(address)
        Dim transactionHash As String = CreateBlockchainTransaction(privateKey, tokenSymbol, balance, exchangeLedgerAddress)
        If Not String.IsNullOrEmpty(transactionHash) Then
            UpdateUserBalance(userId, tokenSymbol, balance) ' Call UpdateUserBalance here
            Console.WriteLine($"Transferred {balance} {tokenSymbol} from {address} to exchange ledger. Transaction hash: {transactionHash}")
        Else
            Console.WriteLine($"Failed to transfer tokens from {address} to exchange ledger.")
        End If
    End Sub
    Private Function GetPrivateKeyForAddress(depositAddress As String) As String
        Dim encryptedPrivateKey As String = _databaseManager.GetEncryptedPrivateKey(depositAddress)
        Dim iv As String = _databaseManager.GetPrivateKeyIV(depositAddress)
        Return WalletHandler.DecryptPrivateKey(encryptedPrivateKey, iv, "112233") ' Replace with your actual encryption key
    End Function
    Public Function RegisterUser(registrationData As RegistrationData) As (Integer, String)
        Using connection As New SqliteConnection(_connectionString) ' Create and open the connection here
            connection.Open()
            Try
                If registrationData Is Nothing OrElse String.IsNullOrEmpty(registrationData.Username) OrElse String.IsNullOrEmpty(registrationData.Password) Then
                    Console.WriteLine("Invalid registration data: Username or password is missing.")
                    Return (-1, "")
                End If
                If _databaseManager.UserExists(registrationData.Username) Then  ' Pass the connection
                    Console.WriteLine("Username already exists: " & registrationData.Username)
                    Return (-1, "")
                End If
                Dim userId As Integer = _databaseManager.CreateUser(registrationData) ' Pass the connection
                Dim apiKey As String = GenerateApiKey() ' Generate the API Key
                _apiKeys.Add(apiKey, userId)
                _databaseManager.StoreApiKey(userId, apiKey) ' Pass the connection here!
                Console.WriteLine("User registered successfully. userId: " & userId & ", apiKey: " & apiKey)
                GenerateDepositAddress(userId)
                Return (userId, apiKey)
            Catch ex As Exception
                Console.WriteLine("Error in RegisterUser: " & ex.Message)
                Return (-1, "")
            End Try
        End Using ' The "Using" block ensures the connection is closed automatically
    End Function
    Public Function LoginUser(loginData As LoginData) As (Integer, String)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT UserId, PasswordHash FROM Users WHERE Username = @Username", connection)
                command.Parameters.AddWithValue("@Username", loginData.Username)
                Using reader As SqliteDataReader = command.ExecuteReader()
                    If reader.Read() Then
                        Dim userId As Integer = reader.GetInt32(0)
                        Dim storedHash As String = reader.GetString(1)
                        If WalletHandler.VerifyPassword(loginData.Password, storedHash) Then
                            ' Retrieve existing API Key
                            Dim apiKey As String = _databaseManager.GetApiKey(userId)
                            If String.IsNullOrEmpty(apiKey) Then
                                ' Generate a new key (this should only happen if there was a problem)
                                apiKey = GenerateApiKey()
                                _databaseManager.StoreApiKey(userId, apiKey) ' Store it in the database
                            End If
                            ' Check if the key is already in the dictionary before adding it
                            If Not _apiKeys.ContainsKey(apiKey) Then
                                _apiKeys.Add(apiKey, userId) ' Add to the in-memory dictionary ONLY if not already there
                            End If
                            Return (userId, apiKey)
                        Else
                            Console.WriteLine("Incorrect password for user: " & loginData.Username)
                            Return (-1, "")
                        End If
                    Else
                        Console.WriteLine("User not found: " & loginData.Username)
                        Return (-1, "")
                    End If
                End Using
            End Using
        End Using
        Return (-1, "")
    End Function

    Public Function GetBalance(userId As Integer, asset As String) As Decimal
        Return _databaseManager.GetUserBalance(userId, asset)
    End Function

    Public Async Function Withdraw(userId As Integer, withdrawalData As WithdrawalData) As Task(Of Boolean)
        ' 1. Validate withdrawal data
        If withdrawalData.Amount <= 0 Then Return False
        ' 2. Check user's balance in the database
        Dim balance As Decimal = GetBalance(userId, withdrawalData.Asset)
        If balance < withdrawalData.Amount Then Return False ' Insufficient balance
        ' 3. Update user's balance in the database (subtract withdrawal amount)
        _databaseManager.UpdateBalance(userId, withdrawalData.Asset, balance - withdrawalData.Amount)
        ' 4. Get exchange wallet's private key and perform the transfer
        Dim exchangePrivateKey As String = _databaseManager.GetExchangePrivateKey() ' Get the exchange's private key
        Dim transactionHash As String = CreateBlockchainTransaction(exchangePrivateKey, withdrawalData.Asset, withdrawalData.Amount, withdrawalData.Address)
        If String.IsNullOrEmpty(transactionHash) Then
            Return False ' Transaction failed
        End If
        Return True ' Withdrawal successful
    End Function

    Public Function GetTokensOwned(userId As Integer) As Dictionary(Of String, Decimal)
        Dim tokensOwned As New Dictionary(Of String, Decimal)
        ' (Implementation to query your database and populate tokensOwned)
        Return tokensOwned
    End Function

    Public Function GetTokenNames() As Dictionary(Of String, String)
        Dim tokenNames As New Dictionary(Of String, String)
        ' 1. Retrieve token names from your blockchain
        Dim tokenNamesToken As JToken = _apiClient.GettAsync("/get_token_names").Result
        If tokenNamesToken.Type = JTokenType.Array Then
            ' Parse the token names from the response
            tokenNames = tokenNamesToken.ToObject(Of JArray)().ToDictionary(
Function(t) t("symbol").ToString(),
Function(t) t("name").ToString()
)
        Else
            ' Handle the case where the response is not an array
            Console.WriteLine("API response for /get_token_names is not a valid array.")
            ' You might want to throw an exception or return an empty dictionary here
        End If
        Return tokenNames
    End Function

    Private Function CreateBlockchainTransaction(privateKey As String, asset As String, amount As Decimal, toAddress As String) As String
        Console.WriteLine("Starting CreateBlockchainTransaction...") ' Log start
        Console.WriteLine($"Private Key (truncated): {privateKey.Substring(0, 10)}...") ' Log part of private key (for debugging)
        Console.WriteLine($"Asset: {asset}, Amount: {amount}, To Address: {toAddress}") ' Log parameters
        Try
            Dim publicKey = WalletHandler.GetPublicKeyFromPrivateKey(privateKey)
            Console.WriteLine($"Public Key: {publicKey}") ' Log public key

            Dim transactionData As String = $"{publicKey}:{toAddress}:{amount}:{asset}"
            Console.WriteLine($"Transaction Data: {transactionData}") ' Log transaction data

            Dim signature As String = WalletHandler.SignTransaction(privateKey, transactionData)
            Console.WriteLine($"Signature (truncated): {signature.Substring(0, 10)}...") ' Log signature
            Console.WriteLine("Calling TransferTokensAsync...") ' Log before API call
            Dim response = _apiClient.TransferTokensAsync(publicKey, toAddress, amount, asset, signature).Result
            Console.WriteLine($"TransferTokensAsync Response: {response.ToString()}") ' Log the entire response

            Return response("txId").ToString()

        Catch ex As Exception
            Console.WriteLine($"Error in CreateBlockchainTransaction: {ex.Message}") ' Log exceptions
            Return String.Empty ' Return empty string on error
        End Try
    End Function

    Public Function GetDepositAddress(userId As Integer) As String
        ' 1. Check if a deposit address already exists for the user
        Dim existingAddress As String = _databaseManager.GetDepositAddress(userId)
        If Not String.IsNullOrEmpty(existingAddress) Then
            Return existingAddress
        End If
        ' 2. If no address exists, generate one
        Return GenerateDepositAddress(userId)
    End Function

    ' Helper function to get balance from blockchain API response
    Private Function GetBalanceFromBlockchainResponse(jsonObject As JObject, asset As String) As Decimal
        If jsonObject Is Nothing OrElse jsonObject("tokensOwned") Is Nothing OrElse jsonObject("tokensOwned")(asset) Is Nothing Then
            Return 0
        End If
        Return jsonObject("tokensOwned")(asset).ToObject(Of Decimal)()
    End Function

    Private Function GenerateApiKey() As String
        Dim keyBytes(31) As Byte ' Create a byte array of size 32
        RandomNumberGenerator.Create.GetBytes(keyBytes) ' Fill the array with random bytes
        Return Convert.ToBase64String(keyBytes)
    End Function

    Private Function GenerateDepositAddress(userId As Integer) As String
        ' Generate a new wallet (key pair)
        Dim wallet As Tuple(Of String, String) = WalletHandler.GenerateWallet()
        Dim publicKey As String = wallet.Item1
        Dim privateKey As String = wallet.Item2
        ' Encrypt the private key
        Dim encryptedKeyAndIV As Tuple(Of String, String) = WalletHandler.EncryptPrivateKey(privateKey, "112233") ' Replace with your actual encryption key
        Dim encryptedPrivateKey As String = encryptedKeyAndIV.Item1
        Dim iv As String = encryptedKeyAndIV.Item2
        ' Store the address and encrypted private key in the database
        _databaseManager.StoreDepositAddress(userId, publicKey, encryptedPrivateKey, iv)
        Return publicKey
    End Function

    Private Sub LoadApiKeys()
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using command As New SqliteCommand("SELECT UserId, ApiKey FROM ApiKeys", connection)
                Using reader As SqliteDataReader = command.ExecuteReader()
                    While reader.Read()
                        Dim userId As Integer = reader.GetInt32(0)
                        Dim apiKey As String = reader.GetString(1)
                        _apiKeys.Add(apiKey, userId)
                    End While
                End Using
            End Using
        End Using
    End Sub

    ' Update the user's balance in the database
    Private Sub UpdateUserBalance(userId As Integer, tokenSymbol As String, amount As Decimal)
        Using connection As New SqliteConnection(_connectionString)
            connection.Open()
            Using transaction As SqliteTransaction = connection.BeginTransaction()
                Try
                    ' 1. Get the current balance from the database
                    Dim currentBalance As Decimal = GetBalance(userId, tokenSymbol)
                    ' 2. Calculate the new balance
                    Dim newBalance As Decimal = currentBalance + amount
                    ' 3. Update the balance in the database
                    UpdateBalanceInDatabase(userId, tokenSymbol, newBalance, connection, transaction)
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
    ' New method to get token prices from the database
    Public Function GetTokenPrices(tokenSymbol As String, fromTimestamp As Long, toTimestamp As Long) As List(Of TokenPrice)
        Return _databaseManager.GetTokenPricesDB(tokenSymbol, fromTimestamp, toTimestamp)
    End Function



    ' New method to get the *latest* token price from the database
    Public Function GetLatestTokenPrice(tokenSymbol As String) As List(Of TokenPrice)
        Return _databaseManager.GetLatestTokenPrice(tokenSymbol)
    End Function

    Private Function CalculateMidPrice(tokenSymbol As String) As Decimal
        ' Get the best buy and sell orders for the given token.
        Dim bestBuyOrder = _buyOrders.OrderByDescending(Function(o) o.Price).ThenBy(Function(o) o.OrderId).FirstOrDefault(Function(o) o.TokenSymbol = tokenSymbol)
        Dim bestSellOrder = _sellOrders.OrderBy(Function(o) o.Price).ThenBy(Function(o) o.OrderId).FirstOrDefault(Function(o) o.TokenSymbol = tokenSymbol)

        ' Get the last traded price (make sure it's nullable)
        Dim lastTradedPrice As Decimal? = _databaseManager.GetLatestTokenPrice(tokenSymbol).FirstOrDefault()?.Price

        ' Handle cases where one or both sides of the book are empty.
        If bestBuyOrder Is Nothing AndAlso bestSellOrder Is Nothing Then
            If lastTradedPrice.HasValue Then
                Return lastTradedPrice.Value ' Use Last Traded Price
            Else
                Return 1D ' Default price if no trades and no orders
            End If
        ElseIf bestBuyOrder Is Nothing Then
            Return bestSellOrder.Price 'Only sell order remains
        ElseIf bestSellOrder Is Nothing Then
            Return bestBuyOrder.Price 'Only buy order remains
        End If

        ' Calculate and return the mid-price.
        Return (bestBuyOrder.Price + bestSellOrder.Price) / 2D
    End Function

    '' New Method: PlaceOrder (MODIFIED)
    Public Function PlaceOrder(userId As Integer, tokenSymbol As String, isBuyOrder As Boolean, quantity As Decimal, price As Decimal?) As Guid
        'Basic Validations
        If quantity <= 0 Then
            Throw New ArgumentException("Quantity must be positive.")
        End If
        If String.IsNullOrWhiteSpace(tokenSymbol) Then
            Throw New ArgumentException("TokenSymbol cannot be empty.")
        End If

        ' Determine the order price
        Dim orderPrice As Decimal
        If price.HasValue Then
            ' Limit order (user provided a price)
            orderPrice = price.Value
            If orderPrice <= 0 Then
                Throw New ArgumentException("Price must be positive.")
            End If
        Else
            ' Market order (use current market price)
            ' Instead of getting the last traded price, get the MID PRICE:
            orderPrice = CalculateMidPrice(tokenSymbol)
            If orderPrice = 0 Then
                orderPrice = 1 'Default price
            End If
        End If

        ' --- BALANCE CHECK (for sell orders) ---
        If Not isBuyOrder Then ' Only check balance for SELL orders
            Dim userBalance As Decimal = _databaseManager.GetUserBalance(userId, tokenSymbol)
            If userBalance < quantity Then
                Throw New ArgumentException("Insufficient balance to place this sell order.")
            End If
            ' "Freeze" the tokens by updating the balance *before* adding the order
            _databaseManager.UpdateBalance(userId, tokenSymbol, userBalance - quantity)
        Else
            ' --- BALANCE CHECK (for buy orders) ---
            'Buy Order: Check USD balance and "freeze" funds
            Dim requiredUsd As Decimal = quantity * orderPrice
            Dim userUsdBalance As Decimal = _databaseManager.GetUserBalance(userId, "USD")
            If userUsdBalance < requiredUsd Then
                Throw New ArgumentException("Insufficient USD balance to place this buy order.")
            End If
            ' "Freeze" the USD by updating the balance *before* adding the order
            _databaseManager.UpdateBalance(userId, "USD", userUsdBalance - requiredUsd)
        End If
        ' --- END BALANCE CHECK ---

        ' Create the Order object - ***CORRECT TIMESTAMP HERE***
        Dim order As New Order(userId, tokenSymbol, isBuyOrder, quantity, orderPrice) With {
            .Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }


        If isBuyOrder Then
            ' Add to buy orders
            _buyOrders.Add(order)
            _databaseManager.AddOrder(order)
        Else
            ' Add to sell orders
            _sellOrders.Add(order)
            _databaseManager.AddOrder(order)
        End If

        MatchOrders(tokenSymbol)
        ' Update the mid-price *after* placing the order and matching
        UpdateMidPrice(tokenSymbol)
        Return order.OrderId
    End Function

    Public Function GetUserOrders(userId As Integer) As List(Of Order)
        Dim userOrders As New List(Of Order)
        userOrders.AddRange(_buyOrders.Where(Function(o) o.UserId = userId))
        userOrders.AddRange(_sellOrders.Where(Function(o) o.UserId = userId))
        Return userOrders
    End Function

    Public Function CancelOrder(userId As Integer, orderId As Guid) As Boolean
        ' Find the order in either the buy or sell orders list
        Dim orderToCancel As Order = _buyOrders.FirstOrDefault(Function(o) o.OrderId = orderId)
        If orderToCancel Is Nothing Then
            orderToCancel = _sellOrders.FirstOrDefault(Function(o) o.OrderId = orderId)
        End If

        ' If order not found, or doesn't belong to the user, return false
        If orderToCancel Is Nothing OrElse orderToCancel.UserId <> userId Then
            Return False
        End If

        ' Remove the order from the appropriate list
        If orderToCancel.IsBuyOrder Then
            _buyOrders.Remove(orderToCancel)
        Else
            _sellOrders.Remove(orderToCancel)
        End If

        ' Remove the order from the database
        _databaseManager.DeleteOrder(orderId)

        ' --- Refund logic ---
        If orderToCancel.IsBuyOrder Then
            ' Refund USD for buy orders
            Dim refundAmount As Decimal = orderToCancel.Quantity * orderToCancel.Price
            _databaseManager.UpdateBalance(userId, "USD", _databaseManager.GetUserBalance(userId, "USD") + refundAmount)
        Else
            ' Refund the token for sell orders
            _databaseManager.UpdateBalance(userId, orderToCancel.TokenSymbol, _databaseManager.GetUserBalance(userId, orderToCancel.TokenSymbol) + orderToCancel.Quantity)
        End If
        ' --- End Refund logic ---

        ' Update the mid-price after canceling the order
        If orderToCancel.TokenSymbol IsNot Nothing Then
            UpdateMidPrice(orderToCancel.TokenSymbol)
        End If
        Return True
    End Function

    ''Modify MatchOrders method
    Private Sub MatchOrders(tokenSymbol As String)
        ' 1. Sort Orders (Price-Time Priority)
        _buyOrders.Sort(Function(x, y)
                            Dim priceComparison = y.Price.CompareTo(x.Price) ' Descending price
                            If priceComparison <> 0 Then Return priceComparison
                            Return x.OrderId.CompareTo(y.OrderId) ' Ascending OrderId (older orders have lower GUIDs)
                        End Function)

        _sellOrders.Sort(Function(x, y)
                             Dim priceComparison = x.Price.CompareTo(y.Price) ' Ascending price
                             If priceComparison <> 0 Then Return priceComparison
                             Return x.OrderId.CompareTo(y.OrderId) ' Ascending OrderId
                         End Function)
        ' 2. Matching Loop
        While _buyOrders.Count > 0 AndAlso _sellOrders.Count > 0

            Dim bestBuyOrder = _buyOrders.FirstOrDefault(Function(o) o.TokenSymbol = tokenSymbol)
            Dim bestSellOrder = _sellOrders.FirstOrDefault(Function(o) o.TokenSymbol = tokenSymbol)
            'If not buy or sell for the specified token, return
            If bestBuyOrder Is Nothing OrElse bestSellOrder Is Nothing Then
                Return
            End If

            ' Check if a match is possible
            If bestBuyOrder.Price >= bestSellOrder.Price Then
                ' Match found!

                ' Determine trade quantity (minimum of buy and sell quantities)
                Dim tradeQuantity As Decimal = Math.Min(bestBuyOrder.Quantity, bestSellOrder.Quantity)

                ' Determine the trade price (price of the *resting* order - the one that was already in the book)
                Dim tradePrice As Decimal = bestSellOrder.Price

                ' Execute the trade
                ExecuteTrade(bestBuyOrder, bestSellOrder, tradeQuantity, tradePrice)

                ' Update order quantities
                bestBuyOrder.Quantity -= tradeQuantity
                bestSellOrder.Quantity -= tradeQuantity

                ' Update mid-price *AFTER* trade but *BEFORE* removing orders
                UpdateMidPrice(tokenSymbol)

                ' Remove fully filled orders
                If bestBuyOrder.Quantity = 0 Then
                    _buyOrders.Remove(bestBuyOrder)
                    _databaseManager.DeleteOrder(bestBuyOrder.OrderId)
                End If
                If bestSellOrder.Quantity = 0 Then
                    _sellOrders.Remove(bestSellOrder)
                    _databaseManager.DeleteOrder(bestSellOrder.OrderId)
                End If

                ' Update partially filled orders in the database
                If bestBuyOrder.Quantity > 0 Then _databaseManager.UpdateOrder(bestBuyOrder)
                If bestSellOrder.Quantity > 0 Then _databaseManager.UpdateOrder(bestSellOrder)
            Else
                ' No match possible (best buy price is lower than best sell price)
                Exit While
            End If
        End While
    End Sub

    ' New Method: UpdateMidPrice
    Private Sub UpdateMidPrice(tokenSymbol As String)
        Dim midPrice As Decimal = CalculateMidPrice(tokenSymbol)
        ' If no midprice is found, then we keep the previous price
        If midPrice <> 0 Then
            _databaseManager.UpdateLastTradedPrice(tokenSymbol, midPrice)
        End If
    End Sub
    Private Sub LoadOrders()
        ' Load buy orders
        For Each order In _databaseManager.GetOrders(isBuyOrder:=True) '<-- Pass only isBuyOrder
            _buyOrders.Add(order)
        Next

        ' Load sell orders
        For Each order In _databaseManager.GetOrders(isBuyOrder:=False) '<-- Pass only isBuyOrder
            _sellOrders.Add(order)
        Next
    End Sub
    Private Sub ExecuteTrade(buyOrder As Order, sellOrder As Order, tradeQuantity As Decimal, tradePrice As Decimal)

        ' Calculate the potential overpayment by the buyer (for limit orders)
        Dim buyerOverpayment As Decimal = 0D
        If buyOrder.Price > tradePrice Then 'This is a BUY LIMIT order
            buyerOverpayment = (buyOrder.Price - tradePrice) * tradeQuantity
        End If

        ' --- CORRECTED BALANCE UPDATES ---

        ' Update Buyer:  Get tokens, USD already deducted in PlaceOrder. REFUND overpayment.
        _databaseManager.UpdateBalance(buyOrder.UserId, buyOrder.TokenSymbol, _databaseManager.GetUserBalance(buyOrder.UserId, buyOrder.TokenSymbol) + tradeQuantity)
        _databaseManager.UpdateBalance(buyOrder.UserId, "USD", _databaseManager.GetUserBalance(buyOrder.UserId, "USD") + buyerOverpayment) ' REFUND

        ' Update Seller: Get USD, tokens already deducted in PlaceOrder
        _databaseManager.UpdateBalance(sellOrder.UserId, "USD", _databaseManager.GetUserBalance(sellOrder.UserId, "USD") + (tradeQuantity * tradePrice))
        ' --- END CORRECTED BALANCE UPDATES ---

        ' 2. Log the trade
        Console.WriteLine($"TRADE: {tradeQuantity} {buyOrder.TokenSymbol} @ {tradePrice} (Buy Order ID: {buyOrder.OrderId}, Sell Order ID: {sellOrder.OrderId})")

        ' 3.  Record the trade in the database (trade history).
        _databaseManager.AddTrade(buyOrder, sellOrder, tradeQuantity, tradePrice)

    End Sub
    ' New Method: GetOrderBook
    Public Function GetOrderBook() As Object
        ' Create an anonymous object to hold the order book data
        Dim orderBook = New With {
            .BuyOrders = _buyOrders.Select(Function(o) New With {
                .TokenSymbol = o.TokenSymbol,
                .Quantity = o.Quantity,
                .Price = o.Price
            }).ToList(),
            .SellOrders = _sellOrders.Select(Function(o) New With {
                .TokenSymbol = o.TokenSymbol,
                .Quantity = o.Quantity,
                .Price = o.Price
            }).ToList()
        }
        Return orderBook
    End Function
    Public Sub Dispose() Implements IDisposable.Dispose
        _cts.Cancel()
        _monitoringTask.Wait()

        ' Dispose of the price monitoring task
        _priceCts.Cancel()
        _priceMonitoringTask.Wait()
    End Sub
End Class