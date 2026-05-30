package llm

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/json"
	"errors"
	"io"
	"os"
)

// appKey 是 AES-256 加密密钥，硬编码在二进制中。
// 只防磁盘文件扫描，不防反编译。对小众桌面应用足够。
var appKey = [32]byte{
	0x7a, 0x3f, 0x71, 0xe2, 0x5c, 0x9d, 0x0b, 0x46,
	0x1a, 0x5f, 0x33, 0xc8, 0x6e, 0x22, 0x4d, 0x0f,
	0x85, 0xce, 0x1c, 0x29, 0x3f, 0xa7, 0x80, 0xf4,
	0x2e, 0x9c, 0x17, 0xd5, 0x4a, 0x8e, 0xd2, 0x06,
}

// LoadUserConfig 从加密文件读取并解密用户 LLM 配置。
// 文件不存在时返回空的 UserLLMConfig 和 nil error。
func LoadUserConfig(path string) (*UserLLMConfig, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return &UserLLMConfig{}, nil
		}
		return nil, err
	}

	plain, err := decrypt(data)
	if err != nil {
		return nil, errors.New("decrypt config failed: " + err.Error())
	}

	var cfg UserLLMConfig
	if err := json.Unmarshal(plain, &cfg); err != nil {
		return nil, errors.New("parse config failed: " + err.Error())
	}
	return &cfg, nil
}

// SaveUserConfig 加密并写入用户 LLM 配置到文件。
func SaveUserConfig(path string, cfg *UserLLMConfig) error {
	plain, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return err
	}

	enc, err := encrypt(plain)
	if err != nil {
		return err
	}

	return os.WriteFile(path, enc, 0600)
}

func encrypt(plain []byte) ([]byte, error) {
	block, err := aes.NewCipher(appKey[:])
	if err != nil {
		return nil, err
	}

	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}

	nonce := make([]byte, gcm.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return nil, err
	}

	return gcm.Seal(nonce, nonce, plain, nil), nil
}

func decrypt(data []byte) ([]byte, error) {
	block, err := aes.NewCipher(appKey[:])
	if err != nil {
		return nil, err
	}

	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}

	nonceSize := gcm.NonceSize()
	if len(data) < nonceSize {
		return nil, errors.New("ciphertext too short")
	}

	nonce, ciphertext := data[:nonceSize], data[nonceSize:]
	return gcm.Open(nil, nonce, ciphertext, nil)
}
