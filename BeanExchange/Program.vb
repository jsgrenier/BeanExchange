Imports System.Net
Imports System.Threading.Tasks

Module Program
    Sub Main(args As String())
        Dim databaseManager As New DatabaseManager("Data Source=exchange.db;")
        Dim apiClient As New APIClient() ' Assuming you have this class
        Dim exchange As New Exchange(databaseManager, apiClient)
        Dim requestHandler As New RequestHandler(exchange)

        ' --- Server Setup (Corrected Asynchronous Handling) ---
        Dim port As Integer = 7070 ' Or your desired port
        Dim listener As New HttpListener()
        listener.Prefixes.Add($"http://localhost:{port}/") ' Use string interpolation
        listener.Start()
        Console.WriteLine($"Listening for requests on {listener.Prefixes.FirstOrDefault()}")

        ' Use Task.Run to start the listening loop in the background.
        Dim listeningTask As Task = Task.Run(Async Function()
                                                 While listener.IsListening
                                                     Try
                                                         ' Use GetContextAsync() for proper asynchronous operation.
                                                         Dim context As HttpListenerContext = Await listener.GetContextAsync()
                                                         ' Handle the request in a separate task.
                                                         Task.Run(Function() requestHandler.HandleRequest(context))
                                                     Catch ex As HttpListenerException
                                                         ' Handle listener exceptions.
                                                         Console.WriteLine($"HttpListenerException: {ex.Message}")
                                                         Exit While 'prevent infinite error
                                                     Catch ex As Exception
                                                         Console.WriteLine($"Exception: {ex.Message}")
                                                     End Try
                                                 End While
                                             End Function)
        ' Keep the main thread alive.  In a real application, you might do other work here.
        Console.WriteLine("Press Enter to exit.")
        Console.ReadLine()

        listener.Stop()
        listener.Close()
        listeningTask.Wait() 'wait the task
        Console.WriteLine("Server stopped.")
        exchange.Dispose()
    End Sub
End Module