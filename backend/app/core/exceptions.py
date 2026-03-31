"""
统一异常处理
"""
from fastapi import HTTPException, status


class APIException(Exception):
    def __init__(self, code: str, message: str, status_code: int = 400):
        self.code = code
        self.message = message
        self.status_code = status_code


class NotFoundException(APIException):
    def __init__(self, resource: str):
        super().__init__(
            code="NOT_FOUND_001",
            message=f"{resource}不存在",
            status_code=status.HTTP_404_NOT_FOUND
        )


class UnauthorizedException(APIException):
    def __init__(self, message: str = "未授权访问"):
        super().__init__(
            code="AUTH_003",
            message=message,
            status_code=status.HTTP_403_FORBIDDEN
        )


class BadRequestException(APIException):
    def __init__(self, message: str):
        super().__init__(
            code="BAD_REQUEST_001",
            message=message,
            status_code=status.HTTP_400_BAD_REQUEST
        )


class ValidationException(APIException):
    def __init__(self, message: str, details: dict = None):
        super().__init__(
            code="VALIDATION_001",
            message=message,
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY
        )
        self.details = details
