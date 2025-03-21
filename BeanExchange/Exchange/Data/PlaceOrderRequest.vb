
Public Class PlaceOrderRequest
    Public Property TokenSymbol As String
    Public Property IsBuyOrder As Boolean
    Public Property Quantity As Decimal
    Public Property Price As Decimal?   ' Add this line.  Make it Nullable.
End Class