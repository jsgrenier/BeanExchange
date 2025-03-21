Public Class Order
    Public Property OrderId As Guid
    Public Property UserId As Integer
    Public Property TokenSymbol As String
    Public Property IsBuyOrder As Boolean
    Public Property Quantity As Decimal
    Public Property Price As Decimal
    Public Property Timestamp As Long

    ' Parameterized constructor (used when creating orders on the server)
    Public Sub New(userId As Integer, tokenSymbol As String, isBuyOrder As Boolean, quantity As Decimal, price As Decimal)
        OrderId = Guid.NewGuid()
        Me.UserId = userId
        Me.TokenSymbol = tokenSymbol
        Me.IsBuyOrder = isBuyOrder
        Me.Quantity = quantity
        Me.Price = price
        Me.Timestamp = 0 ' Initialize.  Important to set in the constructor.  Gets set to the correct value in PlaceOrder
    End Sub

    ' Parameterless constructor (required for JSON deserialization)
    Public Sub New()
        OrderId = Guid.NewGuid()
        Timestamp = 0  'Important, we initiate it.
    End Sub

End Class