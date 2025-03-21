﻿Imports System.Net
Imports System.IO
Imports System.Text
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports System.Net.Http
Imports System.Net.Http.Headers ' For MediaTypeHeaderValue

Public Class APIClient

    Private _baseUrl As String

    Public Sub New()
        _baseUrl = "http://localhost:8080/api" ' blockchain address
    End Sub

    Public Async Function IsConnectionFunctionalAsync() As Task(Of Boolean?) ' Return Boolean? (nullable)
        Try
            Using client As New HttpClient()
                Dim url As String = _baseUrl & "/check_validity"
                Dim response As HttpResponseMessage = Await client.GetAsync(url)
                Return response.IsSuccessStatusCode ' Or check for specific success codes
            End Using
        Catch ex As Exception
            Console.WriteLine($"Connection check failed: {ex.Message}")
            Return Nothing ' Return Nothing on error
        End Try
    End Function

    Public Async Function GettAsync(endpoint As String, Optional queryParams As Dictionary(Of String, String) = Nothing) As Task(Of JToken)
        Try
            Dim url As String = _baseUrl & endpoint
            If queryParams IsNot Nothing Then
                url &= "?" & BuildQueryString(queryParams)
            End If

            Using client As New HttpClient()
                Dim response As HttpResponseMessage = Await client.GetAsync(url)
                response.EnsureSuccessStatusCode()

                Dim jsonResponse As String = Await response.Content.ReadAsStringAsync()
                Dim jsonToken As JToken = JToken.Parse(jsonResponse)
                Return jsonToken
            End Using

        Catch ex As Exception
            Console.WriteLine($"Error communicating with blockchain: {ex.Message}")
            Throw New Exception("Blockchain is not reachable.", ex) ' Include inner exception
        End Try
    End Function

    Public Async Function PostAsync(endpoint As String, jsonData As String) As Task(Of JObject)
        Dim url As String = _baseUrl & endpoint

        Using client As New HttpClient()
            Dim content As New StringContent(jsonData, Encoding.UTF8, "application/json")
            content.Headers.ContentType = New MediaTypeHeaderValue("application/json") ' Explicitly set content type

            Dim response As HttpResponseMessage = Await client.PostAsync(url, content)

            If response.IsSuccessStatusCode Then
                Dim responseJson As String = Await response.Content.ReadAsStringAsync()
                Return JObject.Parse(responseJson)
            ElseIf response.StatusCode = HttpStatusCode.BadRequest Then ' Example: Check for 400 Bad Request
                Dim errorJson As String = Await response.Content.ReadAsStringAsync()
                Console.WriteLine($"Bad Request: {errorJson}")
                Throw New Exception($"Invalid request: {errorJson}") ' Throw a custom exception
            ElseIf response.StatusCode = HttpStatusCode.InternalServerError Then ' Example: Check for 500
                Dim errorJson As String = Await response.Content.ReadAsStringAsync()
                Console.WriteLine($"Internal Server Error: {errorJson}")
                Throw New Exception($"Server Error: {errorJson}")
            ElseIf response.StatusCode = HttpStatusCode.Unauthorized Then ' Example: Check for 401
                Dim errorJson As String = Await response.Content.ReadAsStringAsync()
                Console.WriteLine($"Unauthorized: {errorJson}")
                Throw New Exception($"Unauthorized: {errorJson}")
            Else
                Console.WriteLine($"API Error: {response.StatusCode} - {Await response.Content.ReadAsStringAsync()}")
                Throw New Exception("Error in API request.") ' Generic error
            End If
        End Using
    End Function

    Public Async Function TransferTokensAsync(fromAddress As String, toAddress As String, amount As Decimal, tokenSymbol As String, signature As String) As Task(Of JObject)
        Try
            Dim data As New Dictionary(Of String, String) From {
                {"toAddress", toAddress},
                {"amount", amount.ToString()},
                {"tokenSymbol", tokenSymbol},
                {"signature", signature},
                {"fromAddress", fromAddress}
            }

            Dim jsonData As String = Newtonsoft.Json.JsonConvert.SerializeObject(data)

            Return Await PostAsync("/transfer_tokens", jsonData)

        Catch ex As Exception
            Console.WriteLine($"Error transferring tokens: {ex.Message}")
            Throw New Exception($"Error transferring tokens: {ex.Message}", ex) ' Include inner exception
        End Try
    End Function

    Public Async Function CreateTokenAsync(name As String, symbol As String, supply As String, publickey As String, signature As String) As Task(Of JObject)
        Try
            Dim data As New Dictionary(Of String, String) From {
                {"name", name},
                {"symbol", symbol},
                {"initialSupply", supply},
                {"ownerPublicKey", publickey},
                {"signature", signature}
            }
            Dim jsonData As String = Newtonsoft.Json.JsonConvert.SerializeObject(data)

            Return Await PostAsync("/create_token", jsonData)

        Catch ex As Exception
            Console.WriteLine($"Error creating tokens: {ex.Message}")
            Throw
        End Try
    End Function

    Private Function BuildQueryString(queryParams As Dictionary(Of String, String)) As String
        Dim queryString As String = ""
        For Each param In queryParams
            queryString &= $"{param.Key}={param.Value}&"
        Next
        Return queryString.TrimEnd("&")
    End Function

    Public Async Function CheckBlockchainValidityAsync() As Task(Of Boolean)
        Try
            Dim response As JObject = Await GettAsync("/check_validity")
            Return response("isValid").ToObject(Of Boolean)()
        Catch ex As Exception
            Console.WriteLine($"Error checking blockchain validity: {ex.Message}")
            Return False
        End Try
    End Function
End Class