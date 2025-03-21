Public Class Order
    Public Property OrderId As Guid
    Public Property UserId As Integer
    Public Property TokenSymbol As String
    Public Property IsBuyOrder As Boolean
    Public Property Quantity As Decimal
    Public Property Price As Decimal

    Public Sub New(userId As Integer, tokenSymbol As String, isBuyOrder As Boolean, quantity As Decimal, price As Decimal)
        OrderId = Guid.NewGuid()
        Me.UserId = userId        ' FIXED!
        Me.TokenSymbol = tokenSymbol ' FIXED!
        Me.IsBuyOrder = isBuyOrder  ' FIXED!
        Me.Quantity = quantity    ' FIXED!
        Me.Price = price        ' FIXED!
    End Sub
End Class