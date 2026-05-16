"""通用分页模型"""
from typing import Annotated, Generic, Sequence, TypeVar

from fastapi import Depends
from pydantic import BaseModel, Field

T = TypeVar("T")


class PageResponse(BaseModel, Generic[T]):
    items: Sequence[T]
    total: int
    page: int
    size: int
    total_pages: int = 0

    def __init__(self, **data):
        super().__init__(**data)
        self.total_pages = (self.total + self.size - 1) // self.size


class PaginationParams(BaseModel):
    page: int = Field(1, ge=1)
    size: int = Field(10, ge=1, le=100)

    @property
    def offset(self) -> int:
        return (self.page - 1) * self.size

    @property
    def limit(self) -> int:
        return self.size


PaginationDep = Annotated[PaginationParams, Depends()]
