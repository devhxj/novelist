package storage

import (
	"testing"
)

func TestHasNonZeroPK_AllZero(t *testing.T) {
	if hasNonZeroPK(map[string]any{"id": 0}) {
		t.Error("zero value should return false")
	}
	if hasNonZeroPK(map[string]any{"id": int64(0)}) {
		t.Error("zero int64 should return false")
	}
}

func TestHasNonZeroPK_NonZero(t *testing.T) {
	if !hasNonZeroPK(map[string]any{"id": 5}) {
		t.Error("non-zero should return true")
	}
}

func TestHasNonZeroPK_Mixed(t *testing.T) {
	// 复合主键中任一为零则 false
	if hasNonZeroPK(map[string]any{"novel_id": 1, "scope": ""}) {
		t.Error("empty string = zero → should return false")
	}
}

func TestHasNonZeroPK_Empty(t *testing.T) {
	if hasNonZeroPK(map[string]any{}) {
		t.Error("empty map should return false")
	}
	if hasNonZeroPK(nil) {
		t.Error("nil should return false")
	}
}

func TestToJSON_Struct(t *testing.T) {
	type S struct {
		Name string `json:"name"`
		Age  int    `json:"age"`
	}
	json := toJSON(S{Name: "test", Age: 10})
	if json != `{"name":"test","age":10}` && json != `{"age":10,"name":"test"}` {
		t.Errorf("unexpected JSON: %s", json)
	}
}

func TestToJSON_Nil(t *testing.T) {
	if toJSON(nil) != "null" {
		t.Errorf("nil should be 'null', got %s", toJSON(nil))
	}
}

func TestToJSON_Invalid(t *testing.T) {
	// channel is not JSON-marshalable
	if s := toJSON(make(chan int)); s != "" {
		t.Errorf("unmarshalable should return empty string, got %s", s)
	}
}
