package app

import (
	"novel/internal/location"
)

// GetLocations 返回指定小说的全部地点，供前端侧边栏嵌套树和关系图节点渲染。
func (a *App) GetLocations(novelID int64) ([]location.Location, error) {
	return a.location.ListAllByNovel(a.ctx, novelID)
}

// GetLocationRelations 返回指定小说的全部空间关系（无向边），供前端关系图渲染。
func (a *App) GetLocationRelations(novelID int64) ([]location.LocationRelation, error) {
	return a.location.ListRelationsByNovel(a.ctx, novelID)
}
