Imports System
Imports System.Collections.Generic
Imports System.Net
Imports System.Text.Json
Imports System.Threading.Tasks
Imports System.IO
Imports System.Runtime
Imports System.Net.Http
Imports Newtonsoft.Json.Linq
Imports Org.BouncyCastle.Asn1.Ocsp
Imports Org.BouncyCastle.Utilities.Net

Public Class RequestHandler

    Private ReadOnly _exchange As Exchange
    Private ReadOnly _jsonOptions As New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True} ' Reuse options

    Public Sub New(exchange As Exchange)
        _exchange = exchange
    End Sub

    Public Async Function HandleRequest(context As HttpListenerContext) As Task
        Dim request As HttpListenerRequest = context.Request
        Dim response As HttpListenerResponse = context.Response
        response.ContentType = "application/json"

        Console.WriteLine("Incoming request: " & request.Url.PathAndQuery)

        Try
            Await DispatchRequest(context) ' Centralized request dispatching

        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = ex.Message}, HttpStatusCode.InternalServerError)
            Console.WriteLine("Error in HandleRequest: " & ex.Message)
        End Try
    End Function

    Private Async Function DispatchRequest(context As HttpListenerContext) As Task
        Dim request As HttpListenerRequest = context.Request

        Select Case request.Url.LocalPath.ToLower()
            Case "/register"
                Await HandleRequestAsync(Of RegistrationData)(context, AddressOf _exchange.RegisterUser)
            Case "/login"
                Await HandleRequestAsync(Of LoginData)(context, AddressOf _exchange.LoginUser)
            Case "/balance"
                Await HandleBalance(context)
            Case "/withdraw"
                Await HandleAuthenticatedRequestAsync(Of WithdrawalData)(context, AddressOf _exchange.Withdraw)
            Case "/deposit"
                Await HandleAuthenticatedRequestAsync(context, AddressOf _exchange.GetDepositAddress)
            Case "/getprice"
                Await HandleGetTokenPrice(context)
            Case "/placeorder"
                Await HandlePlaceOrder(context)
            Case "/orders"
                Await HandleGetOrders(context)
            Case "/cancelorder"  ' <-- ADD THIS CASE
                Await HandleCancelOrder(context)
            Case "/myorders"  '<-- ADD THIS
                Await HandleGetMyOrders(context)
            Case "/tokens"  '<-- ADD THIS CASE FOR /tokens endpoint
                Await HandleGetTokens(context)
            Case Else
                Await SendJsonResponse(context.Response, New With {.Error = "Not Found"}, HttpStatusCode.NotFound)
        End Select
    End Function

    Private Async Function HandleGetTokens(context As HttpListenerContext) As Task
        Dim response As HttpListenerResponse = context.Response

        Try
            ' Get the token names from the Exchange.
            Dim tokenNames As Dictionary(Of String, String) = _exchange.GetTokenNames()

            ' Send the response.  We'll send the dictionary directly; it'll be serialized to JSON.
            Await SendJsonResponse(response, tokenNames, HttpStatusCode.OK)

        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = "Failed to retrieve tokens"}, HttpStatusCode.InternalServerError)
            Console.WriteLine("Error in HandleGetTokens: " & ex.Message)
        End Try
    End Function

    Private Async Function HandleCancelOrder(context As HttpListenerContext) As Task
        Dim request As HttpListenerRequest = context.Request
        Dim response As HttpListenerResponse = context.Response

        ' Check for POST method
        If Not request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) Then
            Await SendJsonResponse(response, New With {.Error = "Method Not Allowed"}, HttpStatusCode.MethodNotAllowed)
            Return
        End If

        ' Get API Key and User ID (Authentication)
        Dim apiKey As String = GetApiKeyFromHeader(context)
        Dim userId As Integer = GetUserIdFromApiKey(apiKey)

        If userId = -1 Then
            Await SendJsonResponse(response, New With {.Error = "Unauthorized"}, HttpStatusCode.Unauthorized)
            Return
        End If

        Try
            ' Deserialize Request Body
            Dim requestBody As String = Await ReadRequestBody(request)
            Dim cancelRequest As CancelOrderRequest = JsonSerializer.Deserialize(Of CancelOrderRequest)(requestBody, _jsonOptions)

            ' Validate Request
            If cancelRequest Is Nothing OrElse String.IsNullOrEmpty(cancelRequest.OrderId) Then
                Await SendJsonResponse(response, New With {.Error = "Invalid request data: OrderId is required"}, HttpStatusCode.BadRequest)
                Return
            End If
            ' Attempt to Cancel the Order
            Dim success As Boolean = _exchange.CancelOrder(userId, Guid.Parse(cancelRequest.OrderId))

            If success Then
                Await SendJsonResponse(response, New With {.Message = "Order canceled successfully"}, HttpStatusCode.OK)
            Else
                Await SendJsonResponse(response, New With {.Error = "Order not found or not owned by user"}, HttpStatusCode.NotFound)
            End If

        Catch ex As FormatException
            ' Handle the case where the OrderId is not a valid GUID
            SendJsonResponse(response, New With {.Error = "Invalid OrderId format"}, HttpStatusCode.BadRequest)
        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = "Failed to cancel order"}, HttpStatusCode.InternalServerError)
            Console.WriteLine("Error in HandleCancelOrder: " & ex.Message)
        End Try
    End Function

    ' New Handler for /getprice
    Private Async Function HandleGetTokenPrice(context As HttpListenerContext) As Task
        Dim request As HttpListenerRequest = context.Request
        Dim response As HttpListenerResponse = context.Response

        ' Check for POST method
        If Not request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) Then
            Await SendJsonResponse(response, New With {.Error = "Method Not Allowed"}, HttpStatusCode.MethodNotAllowed)
            Return
        End If

        Try
            ' Read and deserialize the request body
            Dim requestBody As String = Await ReadRequestBody(request)
            Dim priceRequest As GetPriceRequest = JsonSerializer.Deserialize(Of GetPriceRequest)(requestBody, _jsonOptions)

            ' Validate: tokenSymbol is REQUIRED
            If String.IsNullOrEmpty(priceRequest.tokenSymbol) Then
                Await SendJsonResponse(response, New With {.Error = "Token symbol is required"}, HttpStatusCode.BadRequest)
                Return
            End If

            ' Handle optional 'from' and 'to' timestamps (default if null)
            Dim fromTimestamp As Long = If(priceRequest.from.HasValue, priceRequest.from.Value, 0)
            Dim toTimestamp As Long = If(priceRequest.to.HasValue, priceRequest.to.Value, DateTimeOffset.UtcNow.ToUnixTimeSeconds())

            ' Get the prices from the Exchange
            Dim prices As List(Of TokenPrice)
            If fromTimestamp = 0 AndAlso toTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() Then
                ' No range specified: Get ONLY the LATEST price
                prices = _exchange.GetLatestTokenPrice(priceRequest.tokenSymbol)
            Else
                ' Range specified: Get prices within the range
                prices = _exchange.GetTokenPrices(priceRequest.tokenSymbol, fromTimestamp, toTimestamp) 'CHANGE THIS LINE
            End If


            ' Send the response
            Await SendJsonResponse(response, prices, HttpStatusCode.OK)

        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = "Invalid request body"}, HttpStatusCode.BadRequest) ' More specific error
            Console.WriteLine("Error in HandleGetTokenPrice: " & ex.Message)
        End Try
    End Function

    ' Generic handler for requests with a JSON body
    Private Async Function HandleRequestAsync(Of T)(context As HttpListenerContext, action As Func(Of T, (Integer, String))) As Task
        Dim request As HttpListenerRequest = context.Request
        Dim response As HttpListenerResponse = context.Response

        Try
            Dim requestBody As String = Await ReadRequestBody(request)
            Dim data As T = JsonSerializer.Deserialize(Of T)(requestBody, _jsonOptions)

            If data Is Nothing Then
                Await SendJsonResponse(response, New With {.Error = "Invalid request data"}, HttpStatusCode.BadRequest)
                Return
            End If

            Dim result = action(data)
            If result.Item1 > 0 Then ' Check if the operation was successful
                Await SendJsonResponse(response, New With {.ApiKey = result.Item2}, HttpStatusCode.OK) ' Or whatever success data you return
            Else
                Await SendJsonResponse(response, New With {.Error = "Operation failed"}, HttpStatusCode.BadRequest) ' Generic error message
            End If

        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = ex.Message}, HttpStatusCode.InternalServerError)
            Console.WriteLine("Error in HandleRequestAsync: " & ex.Message)
        End Try
    End Function

    ' Generic handler for authenticated requests with JSON body
    Private Async Function HandleAuthenticatedRequestAsync(Of T)(context As HttpListenerContext, action As Func(Of Integer, T, Task(Of Boolean))) As Task
        Dim request As HttpListenerRequest = context.Request
        Dim response As HttpListenerResponse = context.Response

        Dim apiKey As String = GetApiKeyFromHeader(context)
        Dim userId As Integer = GetUserIdFromApiKey(apiKey)

        If userId = -1 Then
            Await SendJsonResponse(response, New With {.Error = "Unauthorized"}, HttpStatusCode.Unauthorized)
            Return
        End If

        Try
            Dim requestBody As String = Await ReadRequestBody(request)
            Dim data As T = JsonSerializer.Deserialize(Of T)(requestBody, _jsonOptions)

            If data Is Nothing Then
                Await SendJsonResponse(response, New With {.Error = "Invalid request data"}, HttpStatusCode.BadRequest)
                Return
            End If

            Dim success As Boolean = Await action(userId, data) ' Await the Withdraw task

            If success Then
                Await SendJsonResponse(response, New With {.Message = "Success"}, HttpStatusCode.OK)
            Else
                Await SendJsonResponse(response, New With {.Error = "Withdrawal failed (insufficient balance or other issue)"}, HttpStatusCode.BadRequest)
            End If

        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = ex.Message}, HttpStatusCode.InternalServerError)
            Console.WriteLine("Error in HandleAuthenticatedRequestAsync: " & ex.Message)
        End Try
    End Function


    ' Overload for methods without a request body (like deposit)
    Private Async Function HandleAuthenticatedRequestAsync(context As HttpListenerContext, action As Func(Of Integer, String)) As Task
        Dim request As HttpListenerRequest = context.Request
        Dim response As HttpListenerResponse = context.Response

        Dim apiKey As String = GetApiKeyFromHeader(context)
        Dim userId As Integer = GetUserIdFromApiKey(apiKey)

        If userId = -1 Then
            Await SendJsonResponse(response, New With {.Error = "Unauthorized"}, HttpStatusCode.Unauthorized)
            Return
        End If

        Try
            Dim result As String = action(userId) ' Call the action
            Await SendJsonResponse(response, New With {.DepositAddress = result}, HttpStatusCode.OK) ' Or whatever data you are returning

        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = ex.Message}, HttpStatusCode.InternalServerError)
            Console.WriteLine("Error in HandleAuthenticatedRequestAsync (No Body): " & ex.Message)
        End Try
    End Function

    Private Async Function HandleBalance(context As HttpListenerContext) As Task
        Dim request As HttpListenerRequest = context.Request
        Dim response As HttpListenerResponse = context.Response

        Dim apiKey As String = GetApiKeyFromHeader(context)
        Dim userId As Integer = GetUserIdFromApiKey(apiKey)

        If userId = -1 Then
            Await SendJsonResponse(response, New With {.Error = "Unauthorized"}, HttpStatusCode.Unauthorized)
            Return
        End If

        Try
            ' Get the token names
            Dim tokenNames = _exchange.GetTokenNames()

            ' Get the balances for each token from the database
            Dim balances As New Dictionary(Of String, Decimal)
            For Each tokenSymbol In tokenNames.Keys
                balances(tokenSymbol) = _exchange.GetBalance(userId, tokenSymbol)
            Next

            ' Send the JSON response with the balances
            Await SendJsonResponse(response, balances, HttpStatusCode.OK)

        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = ex.Message}, HttpStatusCode.InternalServerError)
            Console.WriteLine("Error in HandleBalance: " & ex.Message)
        End Try
    End Function

    Private Async Function HandlePlaceOrder(context As HttpListenerContext) As Task
        Dim request As HttpListenerRequest = context.Request
        Dim response As HttpListenerResponse = context.Response

        ' Check for POST method
        If Not request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) Then
            Await SendJsonResponse(response, New With {.Error = "Method Not Allowed"}, HttpStatusCode.MethodNotAllowed)
            Return
        End If

        ' 1. Get API Key and User ID
        Dim apiKey As String = GetApiKeyFromHeader(context)
        Dim userId As Integer = GetUserIdFromApiKey(apiKey)

        If userId = -1 Then
            Await SendJsonResponse(response, New With {.Error = "Unauthorized"}, HttpStatusCode.Unauthorized)
            Return
        End If

        Try
            ' 2. Read and Deserialize Request Body
            Dim requestBody As String = Await ReadRequestBody(request)
            Dim orderRequest As PlaceOrderRequest = JsonSerializer.Deserialize(Of PlaceOrderRequest)(requestBody, _jsonOptions)

            ' 3. Validate Request (basic validation)
            If orderRequest Is Nothing OrElse String.IsNullOrEmpty(orderRequest.TokenSymbol) OrElse orderRequest.Quantity <= 0 Then
                Await SendJsonResponse(response, New With {.Error = "Invalid order request"}, HttpStatusCode.BadRequest)
                Return
            End If

            ' 4. Place the Order (Call Exchange.PlaceOrder)  <-- MODIFIED
            Dim orderId As Guid = _exchange.PlaceOrder(userId, orderRequest.TokenSymbol, orderRequest.IsBuyOrder, orderRequest.Quantity, orderRequest.Price)

            ' 5. Send Success Response <-- MODIFIED
            Await SendJsonResponse(response, New With {.Message = "Order placed successfully", .OrderId = orderId}, HttpStatusCode.OK)

        Catch ex As ArgumentException
            'Catch specific exception
            SendJsonResponse(response, New With {.Error = ex.Message}, HttpStatusCode.BadRequest)
        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = "Failed to place order"}, HttpStatusCode.InternalServerError)
            Console.WriteLine("Error in HandlePlaceOrder: " & ex.Message)
        End Try
    End Function

    Private Async Function HandleGetOrders(context As HttpListenerContext) As Task
        Dim response As HttpListenerResponse = context.Response

        ' No authentication needed for this simple view (for now)

        Try
            ' Get the order book data from the Exchange
            Dim orderBookData = _exchange.GetOrderBook()

            ' Send the response
            Await SendJsonResponse(response, orderBookData, HttpStatusCode.OK)

        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = "Failed to retrieve order book"}, HttpStatusCode.InternalServerError)
            Console.WriteLine("Error in HandleGetOrders: " & ex.Message)
        End Try
    End Function

    Private Async Function HandleGetMyOrders(context As HttpListenerContext) As Task
        Dim response As HttpListenerResponse = context.Response
        Dim apiKey As String = GetApiKeyFromHeader(context)
        Dim userId As Integer = GetUserIdFromApiKey(apiKey)

        If userId = -1 Then
            Await SendJsonResponse(response, New With {.Error = "Unauthorized"}, HttpStatusCode.Unauthorized)
            Return
        End If

        Try
            Dim orders As List(Of Order) = _exchange.GetUserOrders(userId)
            ' Serialize a simplified version of the orders
            Dim orderData = orders.Select(Function(o) New With {
             .OrderId = o.OrderId,
            .TokenSymbol = o.TokenSymbol,
            .IsBuyOrder = o.IsBuyOrder,
             .Quantity = o.Quantity,
             .Price = o.Price,
             .Timestamp = o.Timestamp
        }).ToList()
            Await SendJsonResponse(response, orderData, HttpStatusCode.OK)
        Catch ex As Exception
            SendJsonResponse(response, New With {.Error = "Failed to retrieve orders"}, HttpStatusCode.InternalServerError)
            Console.WriteLine("Error in HandleGetMyOrders: " & ex.Message)
        End Try
    End Function


    ' Helper Functions (unchanged)

    Private Async Function ReadRequestBody(request As HttpListenerRequest) As Task(Of String)
        Using reader As New StreamReader(request.InputStream)
            Return Await reader.ReadToEndAsync()
        End Using
    End Function

    Private Async Function SendJsonResponse(response As HttpListenerResponse, data As Object, statusCode As HttpStatusCode) As Task
        response.StatusCode = CInt(statusCode)
        Dim jsonString = JsonSerializer.Serialize(data)
        Dim buffer = System.Text.Encoding.UTF8.GetBytes(jsonString)
        response.ContentLength64 = buffer.Length
        Await response.OutputStream.WriteAsync(buffer, 0, buffer.Length)
        response.OutputStream.Close()
    End Function

    Private Function GetApiKeyFromHeader(context As HttpListenerContext) As String
        Return context.Request.Headers.Get("X-API-Key")
    End Function

    Private Function GetUserIdFromApiKey(apiKey As String) As Integer
        If String.IsNullOrEmpty(apiKey) OrElse Not _exchange._apiKeys.ContainsKey(apiKey) Then
            Return -1 ' Indicate unauthorized
        End If
        Return _exchange._apiKeys(apiKey)
    End Function

End Class